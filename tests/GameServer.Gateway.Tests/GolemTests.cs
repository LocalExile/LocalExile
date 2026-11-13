using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class GolemTests
{
    [Fact]
    public void AllPlayableGolemsHaveNativeSkillsAndScaleAtTheirActorLevel()
    {
        var catalog = new GolemCatalog(); var skills = new SkillCatalog(); var monsters = new MonsterCatalog();
        Assert.Equal(17, catalog.All.Count);
        Assert.Null(catalog.Find("SummonLightningGolemAltY"));
        var conn = new ClientState(new(1, "golem-catalog", 1000, 1000, 100, 100, null, []), 1, 1);
        foreach (var definition in catalog.All)
        {
            var variety = monsters.Find(definition.Variety);
            Assert.NotNull(variety); Assert.True(variety.Supported);
            var own = skills.EffectStats(definition.Effect, 20, 20).ToDictionary(p => p.Item1, p => p.Item2);
            int Get(string name) => own.GetValueOrDefault(Program.Stats.Key(name)!.Value);
            Assert.Equal(70, Get("base_display_minion_actor_level"));
            Assert.Equal(definition.Hordes ? 3 : 1, Get("base_number_of_golems_allowed"));
            Assert.Equal(definition.Hordes || definition.Special && definition.Kind != "Stone", definition.Buff == 0);
            var golem = new GolemState.Golem(1, null!, definition, 1) { Own = own };
            var abilities = Game.GolemAbilities(conn, variety, 70, golem, new Dictionary<uint, int>());
            var native = Game.GolemNativeSkills(conn, 70, abilities);
            Assert.Equal(variety.GrantedEffects!.Count, abilities.Count);
            Assert.Equal(abilities.Count, native.Count);
            Assert.Equal(abilities.Count, abilities.Select(a => a.Skill).Distinct().Count());
            Assert.Contains(abilities, a => a.Attack.Damage.MaxTotal > 0);
            Assert.All(abilities, a => Assert.True(a.Attack.Seconds > 0));
            foreach (var (source, target) in definition.BuffStats)
            { Assert.NotNull(Program.Stats.Key(source)); Assert.NotNull(Program.Stats.Key(target)); }
        }
        // One-row monster spells must use actor level, not leave the row's level-3 values in a level-70 golem.
        var low = catalog.Skill(skills, 5025, 34); var high = catalog.Skill(skills, 5025, 70);
        uint cold = Program.Stats.Key("spell_minimum_base_cold_damage")!.Value;
        Assert.True(high.Stats[cold] > low.Stats[cold] * 3);
        var cone = catalog.Skill(skills, 5056, 34);
        Assert.Equal(1, cone.Level);
        Assert.Equal(133, cone.Stats[Program.Stats.Key("spell_minimum_base_fire_damage")!.Value]);
        Assert.Equal(20, catalog.Skill(skills, 5056, 70).Level);
        Assert.Equal(522, catalog.Skill(skills, 4336, 3).Stats[Program.Stats.Key("base_chaos_damage_to_deal_per_minute")!.Value]);
        var flame = catalog.Find("SummonFlameGolem")!;
        var minion = new GolemState.Golem(1, null!, flame, 1);
        var varietyFlame = monsters.Find(flame.Variety)!;
        var baseline = Game.GolemAbilities(conn, varietyFlame, 70, minion, new Dictionary<uint, int>());
        foreach (string kind in new[] { "global", "attack", "spell" })
            foreach (string bound in new[] { "minimum", "maximum" })
                minion.Own[Program.Stats.Key($"{kind}_{bound}_added_fire_damage")!.Value] = 100;
        var added = Game.GolemAbilities(conn, varietyFlame, 70, minion, new Dictionary<uint, int>());
        Assert.All(added, a => Assert.Equal(baseline.Single(b => b.Effect == a.Effect).Attack.Damage[DamageType.Fire].Min + 100,
            a.Attack.Damage[DamageType.Fire].Min));
        Assert.Equal(3, GolemCatalog.Limit(1, n => n == "number_of_golems_allowed" ? 2 : 0));
        Assert.Equal(4, GolemCatalog.Limit(1, n => n == "base_number_of_golems_allowed" ? 3 : 0));
        Assert.Equal(8, GolemCatalog.Limit(3, n => n switch { "number_of_golems_allowed" => 1, "double_golem_limit" => 1, _ => 0 }));
        var stone = catalog.Find("SummonStoneGolem")!;
        Assert.Equal(70, GolemCatalog.BuffEffect(stone, n => n == "golem_buff_effect_+%" ? 50 : 0,
            n => n == "golem_buff_effect_+%" ? 20 : 0, 1));
        // Recovery buffs have conditional fields absent from ordinary golem buff packets.
        var safeguarding = new GolemState.Golem(0x4000000, null!, catalog.Find("SummonStoneGolemAltY")!, 1);
        safeguarding.OwnerBuff[Program.Stats.Key("melee_damage_removed_from_stone_golem_before_life_or_es_%")!.Value] = 18;
        byte[] guard = Game.GolemOwnerBuffPacket(7, 592, safeguarding);
        Assert.Equal(33, guard.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 18, 0, 0, 0, 0 }, guard[23..]);
    }

    [Fact]
    public async Task GolemsReplaceWithoutStackingBuffsTakeDamageReviveAndLoseTheirSource()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-golems-{Guid.NewGuid():N}.db");
        var previousLogic = Program.GameLogicOrNull; var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        using var wire = new Wire();
        Saves.Open(path); Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = new ClientState(new(1, "golem-runtime", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = "GolemRuntime", Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("golem-test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        conn.Golems.Restored = true;
        foreach (string stat in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
            conn.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;

        async Task SharedCharacterGolemSourceAba()
        {
            // Independent loopback clients load the same saved character (A/B) and a distinct control character (C).
            // A/B share the production inventory instance, while every connection keeps its own send gate and local leases.
            const string character = "SharedCharacterGolemSourceAba";
            const string controlCharacter = "SharedCharacterGolemSourceControl";
            const byte witchRow = 1;
            var characterId = Guid.Parse("504c3e3b-0000-4000-8000-000000000504");
            var controlCharacterId = Guid.Parse("504c3e3b-0000-4000-8000-000000000505");
            var schedule = new AreaClockSchedule { At = 600 };
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var previousContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = updates;
            using var aWire = new Wire();
            using var bWire = new Wire();
            using var cWire = new Wire();
            using var dWire = new Wire();
            ClientState? a = null, b = null, c = null, d = null;
            try
            {
                var witch = Program.Skills.ClassFor(witchRow);
                Assert.Equal(3, witch.IntegerId);
                PlayerInventories.SetClass(character, witchRow);
                PlayerInventories.SetClass(controlCharacter, witchRow);

                ClientState Open(uint ticket, string name, Guid id, string remote, Wire wire) =>
                    new(conn.Layout, ticket, ticket) {
                        Character = name, CharacterId = id, Account = "shared-source-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
                        PlayerUpdatesState = updates, Pools = new(100, 1, 1, 0, 0),
                        LeagueModule = conn.LeagueModule, Wire = (remote, wire.Stream, wire.Ciphers),
                    };

                var aLive = a = Open(0xC3B0, character, characterId, "shared-source-a", aWire);
                var bLive = b = Open(0xC3B1, character, characterId, "shared-source-b", bWire);
                var cLive = c = Open(0xC3B2, controlCharacter, controlCharacterId, "shared-source-c", cWire);
                aLive.Golems.Restored = true;
                aLive.Summons.Restored = true;
                bLive.Golems.Restored = true;
                bLive.Summons.Restored = true;
                cLive.Golems.Restored = true;
                cLive.Summons.Restored = true;
                aLive.Walk.Teleport(100, 100, schedule.At);
                bLive.Walk.Teleport(100, 100, schedule.At);
                cLive.Walk.Teleport(100, 100, schedule.At);

                var aInventory = global::Game.ItemsOf(aLive);
                var bInventory = global::Game.ItemsOf(bLive);
                var cInventory = global::Game.ItemsOf(cLive);
                Assert.Same(aInventory, bInventory);
                Assert.NotSame(aInventory, cInventory);

                var gemBase = Program.Items.FindGem("Flame Golem")!.Value;
                var gemType = Program.Items.Gem(gemBase.Type)!;
                var flameGem = ItemCatalog.Create(gemBase.Type) with {
                    GemVariant = gemBase.Variant.Index,
                    GemExperience = gemType.ExperienceFor(1),
                };
                var fireballBase = Program.Items.FindGem("Fireball")!.Value;
                var fireballType = Program.Items.Gem(fireballBase.Type)!;
                var fireballGem = ItemCatalog.Create(fireballBase.Type) with {
                    GemVariant = fireballBase.Variant.Index,
                    GemExperience = fireballType.ExperienceFor(1),
                };
                Assert.Equal(1, global::Game.ItemsFor(aLive).GemLevel(flameGem));
                var wand = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with {
                    Sockets = [new(SocketColour.White, flameGem), new(SocketColour.White, fireballGem)],
                };
                var host = aInventory.Add(PlayerInventories.Weapon1, 0, 0, wand);
                var amulet = new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!;
                Assert.Equal("Metadata/Items/Amulets/Amulet7", Program.Items.ByHash(amulet.TypeHash)!.Id);
                aInventory.Add(5, 0, 0, amulet);
                var controlHost = cInventory.Add(PlayerInventories.Weapon1, 0, 0, wand);
                cInventory.Add(5, 0, 0, amulet);
                Assert.Equal(host.Id, controlHost.Id);
                var physicalGem = host.Item.Sockets[0].Gem!;
                Assert.Equal(flameGem.TypeHash, physicalGem.TypeHash);

                void Recalculate(ClientState client)
                {
                    global::Game.EquipmentStatSources(client, global::Game.ItemsOf(client).EquipmentSnapshot());
                    client.Pools = global::Game.PoolsFor(client, client.Pools.Level);
                    client.PlayerLife = client.Pools.Life;
                    client.PlayerMana = client.Pools.Mana;
                }
                Recalculate(aLive);
                Recalculate(bLive);
                Recalculate(cLive);
                Assert.Empty(aLive.PlayerStats);
                Assert.Empty(bLive.PlayerStats);
                Assert.Equal(100, aLive.Pools.Level);
                Assert.True(aLive.Pools.Strength > witch.Strength);
                Assert.True(aLive.Pools.Dexterity > witch.Dexterity);
                Assert.True(aLive.Pools.Intelligence > witch.Intelligence);
                Assert.Equal((aLive.Pools.Life, aLive.Pools.Mana, aLive.Pools.Strength,
                    aLive.Pools.Dexterity, aLive.Pools.Intelligence),
                    (bLive.Pools.Life, bLive.Pools.Mana, bLive.Pools.Strength,
                        bLive.Pools.Dexterity, bLive.Pools.Intelligence));
                Assert.Equal(aLive.Pools.Intelligence, cLive.Pools.Intelligence);
                Assert.False(aLive.Closed || aLive.LeavingArea || aLive.PlayerLife == 0);
                Assert.False(bLive.Closed || bLive.LeavingArea || bLive.PlayerLife == 0);
                Assert.False(cLive.Closed || cLive.LeavingArea || cLive.PlayerLife == 0);
                Assert.False(aLive.LeagueModule!.HardMode);
                Assert.False(bLive.LeagueModule!.HardMode);
                Assert.False(ReferenceEquals(aWire.Stream, bWire.Stream));
                Assert.False(ReferenceEquals(aWire.Ciphers, bWire.Ciphers));

                IReadOnlyList<global::Game.PlayerSkillSource> Sources(ClientState client)
                {
                    var equipment = global::Game.ItemsOf(client).EquipmentSnapshot();
                    return global::Game.PlayerSkillSourcesAt(client.Character!, equipment,
                        global::Game.InactiveItems.GetValueOrDefault(client.Character!),
                        client.LeagueModule!, [], global::Game.WornGrantedSkillsOf(client, equipment));
                }
                global::Game.PlayerSkillSource FlameSource(ClientState client) => Assert.Single(
                    Sources(client), source =>
                        global::Game.SkillsFor(client).EffectId(source.Effect) == "SummonFlameGolem");
                global::Game.SupportedGem Support(ClientState client, ushort source)
                {
                    using var scope = new global::Game.PlayerStatScope(client, fresh: true);
                    return global::Game.SupportedSkill(client, source);
                }
                static bool SameStats(IReadOnlyDictionary<uint, int> left,
                    IReadOnlyDictionary<uint, int> right) =>
                    left.Count == right.Count && left.All(pair =>
                        right.TryGetValue(pair.Key, out int value) && value == pair.Value);

                var prepared = FlameSource(aLive);
                Assert.Equal(prepared.Identity, FlameSource(bLive).Identity);
                Assert.Equal(prepared.Identity, FlameSource(cLive).Identity);
                Assert.True(prepared.Active);
                Assert.True(FlameSource(cLive).Active);
                Assert.Equal(physicalGem, prepared.SourceGem);
                Assert.Equal(host.Id, prepared.Identity.Host);
                Assert.Equal(0, prepared.Identity.Socket);
                var preparedSupport = Support(aLive, prepared.Id);
                Assert.True(preparedSupport.RequirementsMet);
                Assert.Equal((1, 0), (prepared.Level, prepared.Quality));

                var definition = global::Game.GolemData.Find("SummonFlameGolem")!;
                uint aOwnerId = await global::Game.SummonGolem("test", aWire.Stream, aWire.Ciphers, aLive,
                    prepared.Id, definition, (100, 100));
                uint bOwnerId = await global::Game.SummonGolem("test", bWire.Stream, bWire.Ciphers, bLive,
                    prepared.Id, definition, (100, 100));
                uint cOwnerId = await global::Game.SummonGolem("test", cWire.Stream, cWire.Ciphers, cLive,
                    FlameSource(cLive).Id, definition, (100, 100));
                Assert.NotEqual(0u, aOwnerId);
                Assert.NotEqual(0u, bOwnerId);
                Assert.NotEqual(0u, cOwnerId);
                var aOwner = Assert.Single(aLive.Golems.All).Value;
                var bOwner = Assert.Single(bLive.Golems.All).Value;
                var cOwner = Assert.Single(cLive.Golems.All).Value;
                Assert.Equal(aOwnerId, aOwner.Id);
                Assert.Equal(bOwnerId, bOwner.Id);
                Assert.Equal(cOwnerId, cOwner.Id);
                var aToken = aLive.GemSourceLifetimes.Current(prepared.Identity);
                var bToken = bLive.GemSourceLifetimes.Current(prepared.Identity);
                var cToken = cLive.GemSourceLifetimes.Current(prepared.Identity);
                Assert.NotNull(aToken);
                Assert.NotNull(bToken);
                Assert.NotNull(cToken);
                Assert.NotSame(aToken, bToken);
                Assert.NotSame(aToken, cToken);
                Assert.True(aLive.GemSourceLifetimes.IsCurrent(aToken));
                Assert.True(bLive.GemSourceLifetimes.IsCurrent(bToken));
                Assert.True(cLive.GemSourceLifetimes.IsCurrent(cToken));

                // Same host and item id, different socket: its physical mutation must not end socket 0's source.
                var fireballSource = Assert.Single(Sources(aLive), source =>
                    global::Game.SkillsFor(aLive).EffectId(source.Effect) == "Fireball");
                var unrelatedMarker = aInventory.CaptureGemSourceLifetime(fireballSource.Identity);
                Assert.NotNull(unrelatedMarker);
                Assert.False(unrelatedMarker.Retired);
                Console.WriteLine($"[gateway] [test] unrelated socket marker 0x{host.Id:x}:{fireballSource.Identity.Socket}");
                var unrelatedTarget = new InstanceItems.SocketGem(
                    PlayerInventories.InventoryId(PlayerInventories.Weapon1), host.Id, (uint)fireballSource.Identity.Socket);
                await global::Game.UnsocketGem("test", bWire.Stream, bWire.Ciphers, bLive, unrelatedTarget);
                Assert.True(unrelatedMarker.Retired);
                Assert.True(aLive.GemSourceLifetimes.IsCurrent(aToken));
                Assert.True(bLive.GemSourceLifetimes.IsCurrent(bToken));
                Assert.True(cLive.GemSourceLifetimes.IsCurrent(cToken));
                Assert.Same(aOwner, aLive.Golems.All[aOwnerId]);
                Assert.Same(bOwner, bLive.Golems.All[bOwnerId]);
                Assert.Same(cOwner, cLive.Golems.All[cOwnerId]);
                await global::Game.SocketGem("test", bWire.Stream, bWire.Ciphers, bLive, unrelatedTarget);
                Assert.True(aLive.GemSourceLifetimes.IsCurrent(aToken));
                Assert.True(bLive.GemSourceLifetimes.IsCurrent(bToken));
                Assert.True(cLive.GemSourceLifetimes.IsCurrent(cToken));

                var pendingPackets = new List<byte[]>();
                var previousOutgoing = Program.Outgoing;
                Program.Outgoing = (ciphers, bytes) =>
                {
                    if (ReferenceEquals(ciphers, aWire.Ciphers))
                        lock (pendingPackets) pendingPackets.Add(bytes.ToArray());
                    previousOutgoing?.Invoke(ciphers, bytes);
                };
                var pendingObjectId = aLive.NextObjectId;
                var heldGate = Program.SendGates.GetValue(aWire.Ciphers, _ => new(1, 1));
                Assert.True(heldGate.Wait(0));
                bool gateHeld = true;
                Task<uint>? pending = null;
                try
                {
                    pending = global::Game.SummonGolem("test", aWire.Stream, aWire.Ciphers, aLive,
                        prepared.Id, definition, (100, 100));
                    Assert.False(pending.IsCompleted,
                        "the actual A golem summon did not reach the held spawn gate");
                    Assert.Equal(pendingObjectId + 1, aLive.NextObjectId);
                    Assert.Same(aToken, aLive.GemSourceLifetimes.Current(prepared.Identity));

                    var target = new InstanceItems.SocketGem(
                        PlayerInventories.InventoryId(PlayerInventories.Weapon1), host.Id, 0);
                    var refused = target with { Socket = 999u };
                    await global::Game.UnsocketGem("test", bWire.Stream, bWire.Ciphers, bLive, refused);
                    Assert.True(aLive.GemSourceLifetimes.IsCurrent(aToken));
                    Assert.True(bLive.GemSourceLifetimes.IsCurrent(bToken));
                    Assert.Empty(aInventory.Items(PlayerInventories.Cursor));
                    Assert.Equal(fireballGem,
                        aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[1].Gem);
                    Assert.False(pending.IsCompleted);

                    await global::Game.UnsocketGem("test", bWire.Stream, bWire.Ciphers, bLive, target);
                    Assert.Same(aInventory, global::Game.ItemsOf(bLive));
                    Assert.Null(aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem);
                    var heldGem = Assert.Single(aInventory.Items(PlayerInventories.Cursor));
                    Assert.Equal(physicalGem, heldGem.Item);
                    Assert.False(bLive.GemSourceLifetimes.IsCurrent(bToken));
                    Assert.False(aLive.GemSourceLifetimes.IsCurrent(aToken));
                    Assert.True(cLive.GemSourceLifetimes.IsCurrent(cToken));
                    Assert.DoesNotContain(bOwnerId, bLive.Golems.All.Keys);

                    await global::Game.SocketGem("test", bWire.Stream, bWire.Ciphers, bLive, target);
                    Assert.Empty(aInventory.Items(PlayerInventories.Cursor));
                    var restoredHost = Assert.Single(aInventory.Items(PlayerInventories.Weapon1));
                    Assert.Equal(host.Id, restoredHost.Id);
                    Assert.Equal(physicalGem, restoredHost.Item.Sockets[0].Gem);
                    var after = FlameSource(aLive);
                    Assert.Equal(prepared, after);
                    Assert.Equal(prepared.Identity, FlameSource(bLive).Identity);
                    var afterSupport = Support(aLive, after.Id);
                    Assert.Equal((preparedSupport.Effect, preparedSupport.Level, preparedSupport.Quality,
                        preparedSupport.RequirementsMet), (afterSupport.Effect, afterSupport.Level,
                        afterSupport.Quality, afterSupport.RequirementsMet));
                    Assert.True(SameStats(preparedSupport.Stats, afterSupport.Stats));
                    Assert.False(aLive.GemSourceLifetimes.IsCurrent(aToken));
                    Assert.True(cLive.GemSourceLifetimes.IsCurrent(cToken));

                    var afterAbaJson = CharacterSaves.ModuleData(character, "Game.Golems");
                    Assert.True(afterAbaJson is not null,
                        "B's successful same-gem removal/reinsert did not save Game.Golems");
                    var afterAba = JsonSerializer.Deserialize<GolemState.Saved[]>(afterAbaJson!) ?? [];
                    Assert.True(afterAba.Length == 0,
                        $"B saved an activation after the physical source ABA: {afterAbaJson}");

                    string aRoster = string.Join(",", aLive.Golems.All.Keys.Order().Select(id => $"0x{id:x8}"));
                    Console.WriteLine($"[gateway] [test] old A actor 0x{aOwnerId:x8}, expired token current="
                        + $"{aLive.GemSourceLifetimes.IsCurrent(aToken)}, A roster=[{aRoster}]");
                    global::Game.SaveGolems(aLive);
                    string staleSaveJson = CharacterSaves.ModuleData(character, "Game.Golems") ?? "<missing>";
                    var staleSaved = JsonSerializer.Deserialize<GolemState.Saved[]>(staleSaveJson) ?? [];
                    string staleSummary = string.Join("; ", staleSaved.Select(entry =>
                        $"{entry.Effect}:skill={entry.Skill},life={entry.Life},"
                        + $"revive={entry.ReviveSeconds},summoned={entry.SummonedSecondsRemaining}"));
                    Assert.True(staleSaved.Length == 0,
                        "SaveGolems from the old A connection re-saved Game.Golems after the physical source ABA. "
                        + $"saved JSON={staleSaveJson}; parsed=[{staleSummary}]; "
                        + $"old token current={aLive.GemSourceLifetimes.IsCurrent(aToken)}; "
                        + $"old actor=0x{aOwnerId:x8}; pending completed={pending.IsCompleted}; "
                        + $"A gate held={gateHeld}; A roster ({aLive.Golems.All.Count})=[{aRoster}]");

                    // B's actual fresh activation on the reinserted source is newer than A's held preparation.
                    // A's Saves transaction must retain B's exact durable activation and resources.
                    var bCurrent = FlameSource(bLive);
                    uint bCurrentId = await global::Game.SummonGolem("test", bWire.Stream, bWire.Ciphers, bLive,
                        bCurrent.Id, definition, (100, 100));
                    Assert.NotEqual(0u, bCurrentId);
                    var bCurrentActor = Assert.Single(bLive.Golems.All).Value;
                    Assert.Equal(bCurrentId, bCurrentActor.Id);
                    global::Game.SaveGolems(bLive);
                    string bCurrentJson = CharacterSaves.ModuleData(character, "Game.Golems") ?? "<missing>";
                    var bCurrentSaved = Assert.Single(JsonSerializer.Deserialize<GolemState.Saved[]>(bCurrentJson)!);
                    Assert.Equal(definition.Id, bCurrentSaved.Effect);
                    Assert.Equal(bCurrent.Id, bCurrentSaved.Skill);
                    Assert.NotNull(bCurrentSaved.Resources);
                    Assert.True(bCurrentSaved.Resources!.Life > 0);
                    Assert.True(bCurrentSaved.Life > 0);

                    global::Game.SaveGolems(aLive);
                    string afterOldAJson = CharacterSaves.ModuleData(character, "Game.Golems") ?? "<missing>";
                    Assert.Equal(bCurrentJson, afterOldAJson);
                    Assert.False(pending.IsCompleted);

                    uint controlAfterChange = await global::Game.SummonGolem("test", cWire.Stream, cWire.Ciphers,
                        cLive, FlameSource(cLive).Id, definition, (100, 100));
                    Assert.NotEqual(0u, controlAfterChange);
                    Assert.True(cLive.GemSourceLifetimes.IsCurrent(cToken));
                }
                finally
                {
                    if (gateHeld)
                    {
                        heldGate.Release();
                        gateHeld = false;
                    }
                    if (pending is not null && !pending.IsCompleted) await pending;
                    Program.Outgoing = previousOutgoing;
                }

                uint pendingResult = await pending!;
                bool HasCreate(uint id) => pendingPackets.Any(packet =>
                    packet.Length >= 6
                    && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId
                    && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == id);
                bool HasPacket(byte[] expected) => pendingPackets.Any(packet =>
                    packet.AsSpan().IndexOf(expected) >= 0);
                Assert.True(pendingResult == 0u,
                    $"A's pending summon for allocated object 0x{pendingObjectId:x} returned "
                    + $"0x{pendingResult:x}; create={HasCreate(pendingObjectId)}, "
                    + $"wake={HasPacket(InstanceProtocol.EnterWakeZone(pendingObjectId))}, "
                    + $"remove={HasPacket(InstanceProtocol.RemoveObject(pendingObjectId))}");
                Assert.False(HasCreate(pendingObjectId));
                Assert.False(HasPacket(InstanceProtocol.EnterWakeZone(pendingObjectId)));
                Assert.False(HasPacket(InstanceProtocol.RemoveObject(pendingObjectId)));
                Assert.False(aLive.GemSourceLifetimes.IsCurrent(aToken));

                // A fresh A activation after the physical source changed twice is real and may claim its new source.
                // A later save from B, whose actor used the previous physical lifetime, must retain A exactly.
                uint bPreviousId = Assert.Single(bLive.Golems.All).Key;
                var bPrevious = bLive.Golems.All[bPreviousId];
                var bPreviousLease = Assert.IsType<GemSourceLifetimes.SourceLifetime>(bPrevious.GemSource);
                var physicalTarget = new InstanceItems.SocketGem(
                    PlayerInventories.InventoryId(PlayerInventories.Weapon1), host.Id, 0);
                await global::Game.UnsocketGem("test", bWire.Stream, bWire.Ciphers, bLive, physicalTarget);
                await global::Game.SocketGem("test", bWire.Stream, bWire.Ciphers, bLive, physicalTarget);
                Assert.False(bLive.GemSourceLifetimes.IsCurrent(bPreviousLease));
                Assert.DoesNotContain(bPreviousId, bLive.Golems.All.Keys);

                var aCurrent = FlameSource(aLive);
                uint aFreshId = await global::Game.SummonGolem("test", aWire.Stream, aWire.Ciphers, aLive,
                    aCurrent.Id, definition, (100, 100));
                Assert.NotEqual(0u, aFreshId);
                var aFreshToken = aLive.GemSourceLifetimes.Current(aCurrent.Identity);
                Assert.NotNull(aFreshToken);
                Assert.NotSame(aToken, aFreshToken);
                Assert.True(aLive.GemSourceLifetimes.IsCurrent(aFreshToken));
                var aFreshActor = aLive.Golems.All[aFreshId];
                Assert.Same(aFreshToken, aFreshActor.GemSource);
                var killer = new SpawnedMonster("golem reviver", new(100, 100, 1), 100, 8, 1_000_000);
                var modestHit = new MonsterAttack(0x4000, 1, 100,
                    DamagePacket.Of(DamageType.Physical, 100, 100)) { Spell = true };
                Assert.True(global::Game.GolemTakeHitLocked(aLive, aFreshActor, 0x0800c3b1, killer, modestHit,
                    schedule.At));
                Assert.True(aFreshActor.Monster.Life < aFreshActor.Monster.MaxLife);
                global::Game.SaveGolems(aLive);
                string freshAJson = CharacterSaves.ModuleData(character, "Game.Golems") ?? "<missing>";
                var freshASaved = Assert.Single(JsonSerializer.Deserialize<GolemState.Saved[]>(freshAJson)!);
                Assert.NotNull(freshASaved.Resources);
                Assert.Equal(aFreshActor.Monster.Life, freshASaved.Resources!.Life);
                global::Game.SaveGolems(bLive);
                Assert.Equal(freshAJson, CharacterSaves.ModuleData(character, "Game.Golems"));

                // The current A actor dies through the game's own damage/deadline path. A new same-character
                // connection restores the saved waiting actor, claims its writer, and saves the remaining 1.5s.
                // A's late save must retain D's waiting resource state, then D's automatic revival remains the writer.
                var lethal = new MonsterAttack(0x4000, 1, 100,
                    DamagePacket.Of(DamageType.Physical, 1_000_000, 1_000_000)) { Spell = true };
                Assert.True(global::Game.GolemTakeHitLocked(aLive, aFreshActor, 0x0800c3b1, killer, lethal,
                    schedule.At));
                Assert.True(aFreshActor.Monster.Dead);
                Assert.Equal(schedule.At + 2, aFreshActor.ReviveAt);
                global::Game.SaveGolems(aLive);
                global::Game.SaveGolems(bLive);
                var deadPreserved = Assert.Single(JsonSerializer.Deserialize<GolemState.Saved[]>(
                    CharacterSaves.ModuleData(character, "Game.Golems")!)!);
                Assert.Equal(aCurrent.Id, deadPreserved.Skill);
                Assert.Equal(2, deadPreserved.ReviveSeconds);

                schedule.At = 601;
                d = Open(0xC3B3, character, characterId, "shared-source-d", dWire);
                Assert.NotEqual(aLive.VitalsSessionId, d.VitalsSessionId);
                d.Golems.Restored = false;
                d.Summons.Restored = true;
                d.Walk.Teleport(100, 100, schedule.At);
                Recalculate(d);
                await global::Game.GolemsTick("test", dWire.Stream, dWire.Ciphers, d, schedule.At);
                var waiting = Assert.Single(d.Golems.Waiting);
                Assert.Equal(aCurrent.Id, waiting.Saved.Skill);
                Assert.Equal(603, waiting.At);
                var waitingLease = Assert.IsType<GemSourceLifetimes.SourceLifetime>(waiting.GemSource);
                Assert.Same(waitingLease, d.GemSourceLifetimes.Current(aCurrent.Identity));
                Assert.False(waitingLease.Retired);
                var waitingWriter = aInventory.GemActivationWriter("Game.Golems", aCurrent.Id);
                Assert.True(waitingWriter is { } claimedWaiting
                    && claimedWaiting.Owner == d.VitalsSessionId
                    && ReferenceEquals(claimedWaiting.Marker, waitingLease.Parent));
                uint waitingObjectId = d.NextObjectId;

                schedule.At = 601.5;
                global::Game.SaveGolems(d);
                string dWaitingJson = CharacterSaves.ModuleData(character, "Game.Golems") ?? "<missing>";
                var dWaitingSaved = Assert.Single(JsonSerializer.Deserialize<GolemState.Saved[]>(dWaitingJson)!);
                Assert.Equal(aCurrent.Id, dWaitingSaved.Skill);
                Assert.Equal(1.5, dWaitingSaved.ReviveSeconds);
                global::Game.SaveGolems(aLive);
                Assert.Equal(dWaitingJson, CharacterSaves.ModuleData(character, "Game.Golems"));

                schedule.At = 603;
                await global::Game.GolemsTick("test", dWire.Stream, dWire.Ciphers, d, schedule.At);
                Assert.Empty(d.Golems.Waiting);
                var dLive = Assert.Single(d.Golems.All).Value;
                Assert.Equal(waitingObjectId, dLive.Id);
                Assert.False(dLive.Monster.Dead);
                string dLiveJson = CharacterSaves.ModuleData(character, "Game.Golems") ?? "<missing>";
                var dLiveSaved = Assert.Single(JsonSerializer.Deserialize<GolemState.Saved[]>(dLiveJson)!);
                Assert.Equal(0, dLiveSaved.ReviveSeconds);
                Assert.NotNull(dLiveSaved.Resources);
                Assert.True(dLiveSaved.Resources!.Life > 0);
                global::Game.SaveGolems(aLive);
                Assert.Equal(dLiveJson, CharacterSaves.ModuleData(character, "Game.Golems"));

                // The automatic empty roster still publishes its queued native removal and reconciles the owner
                // count and buff cache after saving. The source ABA retires D's exact revival lease first.
                Assert.True(global::Game.GolemTakeHitLocked(d, dLive, 0x0800c3b3, killer, lethal, schedule.At));
                Assert.True(dLive.Monster.Dead);
                Assert.Equal(605, dLive.ReviveAt);
                uint expiredId = dLive.Id;
                var activeGolems = Program.Stats.Key("virtual_total_number_of_active_golems")!.Value;
                Assert.Equal(1, d.Golems.OwnerStats[activeGolems]);
                Assert.NotEmpty(d.Golems.Buffs);
                await global::Game.UnsocketGem("test", bWire.Stream, bWire.Ciphers, bLive, physicalTarget);
                await global::Game.SocketGem("test", bWire.Stream, bWire.Ciphers, bLive, physicalTarget);
                Assert.True(waitingLease.Retired || !d.GemSourceLifetimes.IsCurrent(waitingLease));
                var expiredPackets = new List<byte[]>();
                var previousExpiredOutgoing = Program.Outgoing;
                Program.Outgoing = (ciphers, bytes) =>
                {
                    if (ReferenceEquals(ciphers, dWire.Ciphers))
                        lock (expiredPackets) expiredPackets.Add(bytes.ToArray());
                    previousExpiredOutgoing?.Invoke(ciphers, bytes);
                };
                try
                {
                    schedule.At = dLive.ReviveAt;
                    await global::Game.GolemsTick("test", dWire.Stream, dWire.Ciphers, d, schedule.At);
                }
                finally
                {
                    Program.Outgoing = previousExpiredOutgoing;
                }
                Assert.Empty(d.Golems.All);
                Assert.Empty(d.Golems.Waiting);
                Assert.DoesNotContain(expiredId, d.Golems.All.Keys);
                Assert.Empty(d.Golems.OwnerStats);
                Assert.Empty(d.Golems.Buffs);
                Assert.Contains(expiredPackets, packet =>
                    packet.AsSpan().IndexOf(InstanceProtocol.RemoveObject(expiredId)) >= 0);
                Assert.Empty(JsonSerializer.Deserialize<GolemState.Saved[]>(
                    CharacterSaves.ModuleData(character, "Game.Golems") ?? "[]")!);

                var current = FlameSource(aLive);
                uint freshId = await global::Game.SummonGolem("test", aWire.Stream, aWire.Ciphers, aLive,
                    current.Id, definition, (100, 100));
                Assert.NotEqual(0u, freshId);
                var freshToken = aLive.GemSourceLifetimes.Current(current.Identity);
                Assert.NotNull(freshToken);
                Assert.NotSame(aToken, freshToken);
                Assert.True(aLive.GemSourceLifetimes.IsCurrent(freshToken));

                // Replacing the inventory owner is terminal for every old shared marker and local lease. Loading the
                // same saved character again creates a new owner scope which a real current summon can use.
                var freshMarker = aInventory.CaptureGemSourceLifetime(current.Identity);
                Assert.NotNull(freshMarker);
                Assert.False(freshMarker.Retired);
                PlayerInventories.Invalidate(character);
                Assert.False(aLive.GemSourceLifetimes.IsCurrent(freshToken));
                Assert.True(freshMarker.Retired);
                Assert.Null(aInventory.CaptureGemSourceLifetime(current.Identity));
                var reloadedInventory = global::Game.ItemsOf(aLive);
                Assert.NotSame(aInventory, reloadedInventory);
                Assert.Equal(host.Id, Assert.Single(reloadedInventory.Items(PlayerInventories.Weapon1)).Id);
                Recalculate(aLive);
                var reloadedSource = FlameSource(aLive);
                uint reloadedId = await global::Game.SummonGolem("test", aWire.Stream, aWire.Ciphers, aLive,
                    reloadedSource.Id, definition, (100, 100));
                Assert.NotEqual(0u, reloadedId);
                var reloadedToken = aLive.GemSourceLifetimes.Current(reloadedSource.Identity);
                Assert.NotNull(reloadedToken);
                Assert.NotSame(freshToken, reloadedToken);
                Assert.True(aLive.GemSourceLifetimes.IsCurrent(reloadedToken));
            }
            finally
            {
                if (a is not null) a.Closed = true;
                if (b is not null) b.Closed = true;
                if (c is not null) c.Closed = true;
                if (d is not null) d.Closed = true;
                PlayerInventories.Forget(character);
                PlayerInventories.Forget(controlCharacter);
                global::Game.InactiveItems.TryRemove(character, out _);
                global::Game.InactiveItems.TryRemove(controlCharacter, out _);
                global::Game.AreaUpdateContext.Value = previousContext;
            }
        }

        async Task ReloginOrphanCleanup()
        {
            const string character = "ReloginOrphanCleanup";
            var characterId = Guid.Parse("504c3e3d-0000-4000-8000-000000000504");
            const byte witchRow = 1;
            var schedule = new AreaClockSchedule { At = 650 };
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var previousContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = updates;
            var sharedInstance = new InstanceState();
            using var sourceWire = new Wire();
            using var freshWire = new Wire();
            using var restoredWire = new Wire();
            ClientState? source = null, fresh = null, restored = null;
            try
            {
                PlayerInventories.SetClass(character, witchRow);
                ClientState Open(uint ticket, string remote, Wire wire) =>
                    new(conn.Layout, ticket, ticket)
                    {
                        Character = character, CharacterId = characterId, Account = "relogin-orphan-account",
                        League = "Standard", LeagueForm = "Standard", ClassRow = witchRow, Instance = sharedInstance,
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                        Pools = new(100, 1, 1, 0, 0), LeagueModule = conn.LeagueModule,
                        Wire = (remote, wire.Stream, wire.Ciphers),
                    };

                source = Open(0xC3E3, "relogin-orphan-source", sourceWire);
                fresh = Open(0xC3E4, "relogin-orphan-fresh", freshWire);
                Program.InGame.TryAdd(source, 0);
                Program.InGame.TryAdd(fresh, 0);
                foreach (var client in new[] { source, fresh })
                {
                    client.Golems.Restored = true;
                    client.Summons.Restored = true;
                    client.Walk.Teleport(100, 100, schedule.At);
                }

                // The source connection populated the instance. The fresh connection is a real re-entry that shares the
                // same production world and therefore bypasses PopulateArea, leaving MonstersPlaced false.
                Assert.True(global::Game.ClaimMonsterHost("relogin-orphan-source", source));
                source.MonstersPlaced = true;
                Assert.False(global::Game.ClaimMonsterHost("relogin-orphan-fresh", fresh));
                Assert.False(fresh.MonstersPlaced);
                Assert.Same(source.Instance.World, fresh.Instance.World);

                var inventory = global::Game.ItemsOf(source);
                Assert.Same(inventory, global::Game.ItemsOf(fresh));
                var gemBase = Program.Items.FindGem("Flame Golem")!.Value;
                var gemType = Program.Items.Gem(gemBase.Type)!;
                var flameGem = ItemCatalog.Create(gemBase.Type) with
                {
                    GemVariant = gemBase.Variant.Index,
                    GemExperience = gemType.ExperienceFor(1),
                };
                var wand = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with
                {
                    Sockets = [new(SocketColour.White, flameGem)],
                };
                var host = inventory.Add(PlayerInventories.Weapon1, 0, 0, wand);
                inventory.Add(5, 0, 0, new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                void Recalculate(ClientState client)
                {
                    global::Game.EquipmentStatSources(client, inventory.EquipmentSnapshot());
                    client.Pools = global::Game.PoolsFor(client, client.Pools.Level);
                    client.PlayerLife = client.Pools.Life;
                    client.PlayerMana = client.Pools.Mana;
                }
                Recalculate(source);
                Recalculate(fresh);
                Assert.Empty(source.PlayerStats);
                Assert.Empty(fresh.PlayerStats);

                global::Game.PlayerSkillSource Source(ClientState client)
                {
                    var equipment = inventory.EquipmentSnapshot();
                    return Assert.Single(global::Game.PlayerSkillSourcesAt(client.Character!, equipment,
                        global::Game.InactiveItems.GetValueOrDefault(client.Character!), client.LeagueModule!, [],
                        global::Game.WornGrantedSkillsOf(client, equipment)),
                        candidate => global::Game.SkillsFor(client).EffectId(candidate.Effect) == "SummonFlameGolem");
                }
                var sourceSkill = Source(source);
                var freshSkill = Source(fresh);
                bool Supported(ClientState client, ushort sourceId)
                {
                    using var scope = new global::Game.PlayerStatScope(client, fresh: true);
                    return global::Game.SupportedSkill(client, sourceId).RequirementsMet;
                }
                Assert.True(Supported(source, sourceSkill.Id));
                Assert.True(Supported(fresh, freshSkill.Id));
                Assert.Equal(sourceSkill.Identity, freshSkill.Identity);
                Assert.Equal(sourceSkill.Identity.Host, host.Id);
                Assert.Equal(sourceSkill.SourceGem, host.Item.Sockets[0].Gem);

                var rhoa = global::Game.Monsters.Find("Rhoa")!;
                uint hostileId = await global::Game.SpawnMonster("relogin-orphan-source", sourceWire.Stream,
                    sourceWire.Ciphers, source, rhoa, 70, MonsterRarity.Normal, 105, 100,
                    name: "Unrelated shared Rhoa");
                Assert.NotEqual(0u, hostileId);
                var definition = global::Game.GolemData.Find("SummonFlameGolem")!;
                uint sourceGolemId = await global::Game.SummonGolem("relogin-orphan-source", sourceWire.Stream,
                    sourceWire.Ciphers, source, sourceSkill.Id, definition, (100, 100));
                uint freshGolemId = await global::Game.SummonGolem("relogin-orphan-fresh", freshWire.Stream,
                    freshWire.Ciphers, fresh, freshSkill.Id, definition, (100, 100));
                Assert.NotEqual(0u, sourceGolemId);
                Assert.NotEqual(0u, freshGolemId);
                Assert.NotEqual(sourceGolemId, freshGolemId);

                var world = sharedInstance.World;
                SpawnedMonster sourceGolemObject, freshGolemObject, hostileObject;
                lock (world.Monsters)
                {
                    sourceGolemObject = world.Monsters[sourceGolemId];
                    freshGolemObject = world.Monsters[freshGolemId];
                    hostileObject = world.Monsters[hostileId];
                }
                Assert.True(source.Minions.Owns(sourceGolemId, sourceGolemObject));
                Assert.True(fresh.Minions.Owns(freshGolemId, freshGolemObject));
                Assert.True(world.MonsterOrigins.ContainsKey(hostileId));
                Assert.True(source.MinionOrigins.ContainsKey(sourceGolemId));
                Assert.True(fresh.MinionOrigins.ContainsKey(freshGolemId));

                string activationBefore = CharacterSaves.ModuleData(character, "Game.Golems") ?? "<missing>";
                Assert.Contains(JsonSerializer.Deserialize<GolemState.Saved[]>(activationBefore)!,
                    saved => saved.Effect == definition.Id && saved.Skill == freshSkill.Id);

                var preservedSnapshot = new List<SavedMonster>();
                sharedInstance.Monsters = preservedSnapshot;
                fresh.Closed = true;
                Program.GameLogicOrNull!.Closing(fresh);
                Program.GameLogicOrNull!.Left(fresh);
                sharedInstance.RemovePlayer(fresh.PeerId);
                Assert.Same(preservedSnapshot, sharedInstance.Monsters);

                lock (world.Monsters)
                {
                    Assert.DoesNotContain(freshGolemId, world.Monsters.Keys);
                    Assert.Same(sourceGolemObject, world.Monsters[sourceGolemId]);
                    Assert.Same(hostileObject, world.Monsters[hostileId]);
                    Assert.DoesNotContain(world.Monsters.Values, monster => ReferenceEquals(monster, freshGolemObject));
                }
                Assert.True(source.Minions.Owns(sourceGolemId, sourceGolemObject));
                Assert.True(world.MonsterOrigins.ContainsKey(hostileId));
                Assert.True(source.MinionOrigins.ContainsKey(sourceGolemId));
                string activationAfter = CharacterSaves.ModuleData(character, "Game.Golems") ?? "<missing>";
                Assert.Equal(activationBefore, activationAfter);
                Assert.Contains(JsonSerializer.Deserialize<GolemState.Saved[]>(activationAfter)!,
                    saved => saved.Effect == definition.Id && saved.Skill == freshSkill.Id);

                // The durable activation remains usable by the ordinary restoration path of the next connection.
                restored = Open(0xC3E5, "relogin-orphan-restored", restoredWire);
                restored.Golems.Restored = false;
                restored.Summons.Restored = true;
                restored.Walk.Teleport(100, 100, schedule.At);
                Recalculate(restored);
                await global::Game.GolemsTick("relogin-orphan-restored", restoredWire.Stream,
                    restoredWire.Ciphers, restored, schedule.At);
                var restoredGolem = Assert.Single(restored.Golems.All.Values);
                Assert.Equal(definition.Id, restoredGolem.Definition.Id);
                Assert.Equal(freshSkill.Id, restoredGolem.Source);
                Assert.False(restoredGolem.Monster.Dead);
                Assert.True(restoredGolem.Monster.Life > 0);
                Assert.True(restored.Minions.Owns(restoredGolem.Id, restoredGolem.Monster));
                lock (world.Monsters)
                    Assert.Same(restoredGolem.Monster, world.Monsters[restoredGolem.Id]);
            }
            finally
            {
                foreach (var client in new[] { source, fresh, restored })
                {
                    if (client is null) continue;
                    client.Closed = true;
                    Program.InGame.TryRemove(client, out _);
                }
                PlayerInventories.Forget(character);
                global::Game.InactiveItems.TryRemove(character, out _);
                global::Game.AreaUpdateContext.Value = previousContext;
            }
        }

        async Task PrimordialMightOwnerWindow()
        {
            const string condition = "_if_golem_summoned_in_past_8_seconds";
            const string ownerModId = "IncreasedDamageIfGolemSummonedRecently__1";
            string character = "PrimordialOwner" + Guid.NewGuid().ToString("N");
            var schedule = new AreaClockSchedule();
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var previousAreaUpdateContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = updates;
            ClientState? owner = null;
            try
            {
                owner = new ClientState(new(1, "primordial-owner", 1000, 1000, 100, 100, null, []), 3, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                owner.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    owner.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;

                var might = new UniqueCatalog(Program.Items).Create("Primordial Might", new Random(504))!;
                var ownerRoll = might.Mods.Single(mod => Program.Items.Mods.ByHash(mod.Hash)?.Id == ownerModId);
                var golemRoll = might.Mods.Single(mod =>
                    Program.Items.Mods.ByHash(mod.Hash)?.Id == "IncreasedGolemDamageIfGolemSummonedRecently__1_");
                int coefficient = ownerRoll.Values[0];
                int golemCoefficient = golemRoll.Values[0];
                Assert.InRange(coefficient, 25, 30);
                Assert.InRange(golemCoefficient, 35, 45);
                Assert.Contains(Program.Items.Mods.ByHash(ownerRoll.Hash)!.Stats,
                    stat => stat.Id == "damage_+%" + condition);

                var inventory = global::Game.ItemsOf(owner);
                var jewelNode = Program.Passives.JewelNodeForSlot(0)!.Value;
                owner.Passives.Add(jewelNode);
                var wornMight = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, might);
                var golemGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonRockGolem")!);
                var fireballGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!);
                var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, golemGem), new(SocketColour.White, fireballGem)],
                    Links = [2],
                };
                inventory.Add(PlayerInventories.Weapon1, 0, 0, weapon);
                void Recalculate()
                {
                    global::Game.EquipmentStatSources(owner, inventory.EquipmentSnapshot());
                    owner.Pools = global::Game.PoolsFor(owner, 100);
                    owner.PlayerLife = owner.Pools.Life;
                    owner.PlayerMana = owner.Pools.Mana;
                }
                Recalculate();

                var golemSource = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "SummonStoneGolem");
                var golemDefinition = global::Game.GolemData.Find("SummonStoneGolem")!;
                Assert.Equal("SummonStoneGolem", golemDefinition.Id);
                var fireball = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "Fireball");
                int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
                    stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
                int OwnerDamage() => Stat(global::Game.CharacterPlayerStats(owner), "damage_+%");
                double Hit() => global::Game.SupportedHit(owner, fireball.Id).MinTotal;
                bool Window() => global::Game.ItemConditionFlags(owner, owner.Clock.Elapsed.TotalSeconds)[condition];
                GolemState.Golem CurrentGolem()
                {
                    lock (owner.Monsters)
                    {
                        var current = Assert.Single(owner.Golems.All).Value;
                        Assert.False(current.Monster.Dead);
                        Assert.True(current.Monster.Life > 0);
                        return current;
                    }
                }
                static double GolemDamage(GolemState.Golem golem, int effect) =>
                    golem.Abilities.Single(ability => ability.Effect == effect).Attack.Damage.MinTotal;
                int NativeDamage(GolemState.Golem golem, int effect, string bound)
                {
                    var ability = golem.Abilities.Single(candidate => candidate.Effect == effect);
                    var component = Assert.Single(ability.Attack.Damage.Components, part => part.Range.Max > 0);
                    string kind = ability.Attack.Spell ? "spell" : "attack";
                    string slug = component.Type.ToString().ToLowerInvariant();
                    uint key;
                    if (ability.Attack.Spell)
                        key = Program.Stats.Key($"{kind}_{bound}_base_{slug}_damage")!.Value;
                    else
                    {
                        Assert.Equal(DamageType.Physical, component.Type);
                        key = bound == "minimum" ? StatKeys.MainHandMinPhysical : StatKeys.MainHandMaxPhysical;
                    }
                    var skill = golem.Skills.Single(candidate => candidate.Id == ability.Skill);
                    return Assert.Single(skill.Stats!, stat => stat.Key == key).Value;
                }

                schedule.At = 6;
                using var statsScope = global::Game.PlayerStatsScope(owner);
                int beforeOwner = OwnerDamage();
                double beforeHit = Hit();
                Assert.False(Window());
                Assert.Equal(double.NegativeInfinity, owner.Golems.LastSummoned);

                uint failed = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    golemSource.Id, golemDefinition, (100, 100), current: () => false);
                Assert.Equal(0u, failed);
                Assert.Equal(double.NegativeInfinity, owner.Golems.LastSummoned);
                Assert.False(Window());

                uint summoned = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    golemSource.Id, golemDefinition, (100, 100));
                Assert.NotEqual(0u, summoned);
                Assert.Equal(6, owner.Golems.LastSummoned);
                Assert.True(Window());
                int activeOwner = OwnerDamage();
                double activeHit = Hit();
                Assert.Equal(coefficient, activeOwner - beforeOwner);
                Assert.True(activeHit > beforeHit);
                var activeGolem = CurrentGolem();
                var recentAbility = Assert.Single(activeGolem.Abilities, ability => ability.Effect == 136);
                double activeGolemDamage = recentAbility.Attack.Damage.MinTotal;
                Assert.True(activeGolemDamage > 0);
                Assert.Equal(activeOwner, OwnerDamage());
                Assert.Equal(activeHit, Hit());
                Assert.Equal(6, owner.Golems.LastSummoned);

                var sourcesWithMight = owner.StatSourcesCache;
                Assert.NotNull(sourcesWithMight);
                uint damageKey = Program.Stats.Key("damage_+%")!.Value;
                Assert.Equal(coefficient, sourcesWithMight.Sources.GetValueOrDefault(damageKey));
                inventory.Remove(PlayerInventories.PassiveJewels, wornMight.Id);
                global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, owner, inventory).GetAwaiter().GetResult();
                Assert.True(Window());
                Assert.DoesNotContain(inventory.Items(PlayerInventories.PassiveJewels), item => item.Id == wornMight.Id);
                var sourcesWithoutMight = owner.StatSourcesCache;
                Assert.NotNull(sourcesWithoutMight);
                Assert.NotSame(sourcesWithMight, sourcesWithoutMight);
                Assert.Equal(0, sourcesWithoutMight.Sources.GetValueOrDefault(damageKey));
                int sourceOffOwner = OwnerDamage();
                double sourceOffHit = Hit();
                Assert.Equal(coefficient, activeOwner - sourceOffOwner);
                Assert.Equal(beforeOwner, sourceOffOwner);
                Assert.Equal(beforeHit, sourceOffHit);
                Assert.True(activeHit > sourceOffHit);
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(activeGolem, CurrentGolem());
                double sourceOffGolemDamage = GolemDamage(activeGolem, 136);
                Assert.True(activeGolemDamage > sourceOffGolemDamage);

                inventory.Add(PlayerInventories.PassiveJewels, 0, 0, might);
                global::Game.SendEquipment("test", wire.Stream, wire.Ciphers, owner, inventory).GetAwaiter().GetResult();
                Assert.True(Window());
                Assert.Equal(activeOwner, OwnerDamage());
                Assert.Equal(activeHit, Hit());
                schedule.At = 6.5;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(activeGolem, CurrentGolem());
                Assert.Equal(activeGolemDamage, GolemDamage(activeGolem, 136));
                Assert.Equal(6, owner.Golems.LastSummoned);

                lock (owner.Monsters)
                {
                    var golem = Assert.Single(owner.Golems.All).Value;
                    golem.Monster.Dead = true;
                    golem.Monster.Life = 0;
                    owner.Golems.All.Remove(golem.Id);
                    owner.Minions.Remove(golem.Id);
                    owner.Monsters.Remove(golem.Id);
                    owner.MinionOrigins.TryRemove(golem.Id, out _);
                }
                Assert.True(Window());

                schedule.At = 13.999;
                Assert.True(Window());
                Assert.Equal(activeOwner, OwnerDamage());
                Assert.Equal(activeHit, Hit());
                schedule.At = 14;
                Assert.False(Window());
                Assert.Equal(sourceOffOwner, OwnerDamage());
                Assert.Equal(sourceOffHit, Hit());
                Assert.Equal(beforeOwner, OwnerDamage());
                Assert.Equal(beforeHit, Hit());

                uint restored = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    golemSource.Id, golemDefinition, (100, 100), arrival: true);
                Assert.NotEqual(0u, restored);
                Assert.Equal(6, owner.Golems.LastSummoned);
                Assert.False(Window());
                Assert.Equal(sourceOffOwner, OwnerDamage());
                Assert.Equal(sourceOffHit, Hit());
                var expiredArrival = CurrentGolem();
                Assert.Equal(6, expiredArrival.SummonedAt);
                Assert.Equal(14, expiredArrival.LastStep);
                Assert.Equal(sourceOffGolemDamage, GolemDamage(expiredArrival, 136));

                uint partlyAged = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    golemSource.Id, golemDefinition, (100, 100), arrival: true, summonedSecondsRemaining: 4);
                Assert.NotEqual(0u, partlyAged);
                var agedArrival = CurrentGolem();
                Assert.Equal(10, agedArrival.SummonedAt);
                Assert.Equal(14, agedArrival.LastStep);
                Assert.Equal(golemCoefficient, global::Game.GolemRecentDamageIncrease(agedArrival, schedule.At));
                Assert.True(GolemDamage(agedArrival, 136) > GolemDamage(expiredArrival, 136));
                Assert.True(NativeDamage(agedArrival, 136, "minimum") > NativeDamage(expiredArrival, 136, "minimum"));
                Assert.True(NativeDamage(agedArrival, 136, "maximum") > NativeDamage(expiredArrival, 136, "maximum"));

                foreach (double invalidRemaining in new[] { double.NaN, double.PositiveInfinity })
                {
                    schedule.At = 15;
                    uint invalidArrival = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                        golemSource.Id, golemDefinition, (100, 100), arrival: true,
                        summonedSecondsRemaining: invalidRemaining);
                    Assert.NotEqual(0u, invalidArrival);
                    var invalid = CurrentGolem();
                    Assert.Equal(7, invalid.SummonedAt);
                    Assert.Equal(15, invalid.LastStep);
                    Assert.False(global::Game.GolemSummonedInPastEightSeconds(invalid, schedule.At));
                    Assert.Equal(sourceOffGolemDamage, GolemDamage(invalid, 136));
                }

                schedule.At = 20;
                uint refreshed = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    golemSource.Id, golemDefinition, (100, 100));
                Assert.NotEqual(0u, refreshed);
                Assert.Equal(20, owner.Golems.LastSummoned);
                Assert.True(Window());
                Assert.Equal(coefficient, OwnerDamage() - sourceOffOwner);
                Assert.True(Hit() > sourceOffHit);
                var refreshedGolem = CurrentGolem();
                double refreshedGolemDamage = GolemDamage(refreshedGolem, 136);
                Assert.True(refreshedGolemDamage > sourceOffGolemDamage);
                schedule.At = 27.999;
                Assert.True(Window());
                Assert.Equal(coefficient, OwnerDamage() - sourceOffOwner);
                Assert.True(Hit() > sourceOffHit);
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(refreshedGolem, CurrentGolem());
                Assert.Equal(refreshedGolemDamage, GolemDamage(refreshedGolem, 136));
                schedule.At = 28;
                Assert.False(Window());
                Assert.Equal(sourceOffOwner, OwnerDamage());
                Assert.Equal(sourceOffHit, Hit());
                Assert.Equal(beforeOwner, OwnerDamage());
                Assert.Equal(beforeHit, Hit());
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(refreshedGolem, CurrentGolem());
                Assert.Equal(sourceOffGolemDamage, GolemDamage(refreshedGolem, 136));
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousAreaUpdateContext;
                if (owner is not null)
                {
                    owner.Closed = true;
                    PlayerInventories.Forget(character);
                    global::Game.InactiveItems.TryRemove(character, out _);
                }
            }
        }

        async Task PrimordialHarmonySourceConsumers()
        {
            string character = "PrimordialHarmony" + Guid.NewGuid().ToString("N");
            var schedule = new AreaClockSchedule { At = 10 };
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var priorContext = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = updates;
            ClientState? owner = null;
            try
            {
                owner = new ClientState(new(1, "primordial-harmony", 1000, 1000, 100, 100, null, []), 9, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0), LeagueModule = conn.LeagueModule,
                };
                owner.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                    "virtual_intelligence_for_requirements" }) owner.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                // Prepared capacity permits two real gem-produced types; the item coefficients and entities stay real.
                owner.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                var inventory = Game.ItemsOf(owner);
                var node = Program.Passives.JewelNodeForSlot(0)!.Value; owner.Passives.Add(node);
                var harmony = new UniqueCatalog(Program.Items).Create("Primordial Harmony", new Random(504))!;
                var raw = ItemModStats.Sum([harmony], Program.Items.Mods);
                int damage = raw.GetValueOrDefault("golem_damage_+%_per_active_golem_type");
                int regeneration = raw.GetValueOrDefault("golem_life_regeneration_per_minute_%");
                Assert.True(damage > 0); Assert.True(regeneration > 0);
                var jewel = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, harmony);
                var bow = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonRockGolem")!)),
                        new(SocketColour.White, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!))],
                };
                inventory.Add(PlayerInventories.Weapon1, 0, 0, bow);
                void Recalculate()
                {
                    owner.Pools = Game.PoolsFor(owner, 100); owner.PlayerLife = owner.Pools.Life; owner.PlayerMana = owner.Pools.Mana;
                }
                int Stat(IReadOnlyDictionary<uint, int> stats, string name) => stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
                Recalculate();
                var grants = Game.GemSkillEffects(owner).ToArray();
                var stoneGrant = grants.Single(g => Game.SkillsFor(owner).EffectId(g.Effect) == "SummonStoneGolem");
                var flameGrant = grants.Single(g => Game.SkillsFor(owner).EffectId(g.Effect) == "SummonFlameGolem");
                uint stoneId = await Game.SummonGolem("harmony", wire.Stream, wire.Ciphers, owner, stoneGrant.Id,
                    Game.GolemData.Find("SummonStoneGolem")!, (100, 100));
                Assert.NotEqual(0u, stoneId);
                var stone = owner.Golems.All[stoneId];
                async Task Check(int types, bool active)
                {
                    Recalculate(); schedule.At += 1;
                    await Game.GolemsTick("harmony", wire.Stream, wire.Ciphers, owner, schedule.At);
                    Assert.Same(stone, owner.Golems.All[stoneId]);
                    var player = Game.CharacterPlayerStats(owner);
                    Assert.Equal(types, Stat(player, "virtual_current_number_of_golem_types"));
                    Assert.Equal(active ? damage : 0, Stat(player, "golem_damage_+%_per_active_golem_type"));
                    var without = new Dictionary<uint, int>(player);
                    without.Remove(Program.Stats.Key("golem_damage_+%_per_active_golem_type")!.Value);
                    Assert.Equal(active ? types * damage : 0,
                        Game.GolemModifiers(owner, stone, player, schedule.At).Effects["damage_+%"]
                        - Game.GolemModifiers(owner, stone, without, schedule.At).Effects["damage_+%"]);
                    // A generic fighter uses the shared controller but receives neither golem-only damage nor regen.
                    var generic = new GolemState.Golem(0, stone.Monster, stone.Definition with { IsGolem = false }, stone.Source);
                    Assert.Equal(Game.GolemModifiers(owner, generic, without, schedule.At).Effects["damage_+%"],
                        Game.GolemModifiers(owner, generic, player, schedule.At).Effects["damage_+%"]);
                    stone.Monster.Life = stone.Monster.MaxLife / 2;
                    stone.Regeneration = 0; stone.LastStep = schedule.At;
                    uint before = stone.Monster.Life;
                    schedule.At += 1;
                    await Game.GolemsTick("harmony", wire.Stream, wire.Ciphers, owner, schedule.At);
                    double expected = stone.Monster.MaxLife * (.02 + (active ? regeneration : 0) / 6000d);
                    Assert.InRange((long)stone.Monster.Life - before, (long)Math.Floor(expected) - 1, (long)Math.Ceiling(expected));
                    Assert.Same(stone.Monster, owner.Monsters[stoneId]);
                }
                await Check(1, true);
                uint flameId = await Game.SummonGolem("harmony", wire.Stream, wire.Ciphers, owner, flameGrant.Id,
                    Game.GolemData.Find("SummonFlameGolem")!, (105, 100));
                Assert.NotEqual(0u, flameId); Assert.NotEqual(stoneId, flameId);
                await Check(2, true);
                owner.Passives.Remove(node); await Check(2, false);
                owner.Passives.Add(node); await Check(2, true);
                inventory.Remove(PlayerInventories.PassiveJewels, jewel.Id); await Check(2, false);
            }
            finally
            {
                Game.AreaUpdateContext.Value = priorContext;
                if (owner is not null) owner.Closed = true;
                PlayerInventories.Forget(character); Game.InactiveItems.TryRemove(character, out _);
            }
        }

        async Task PrimordialGolemLifetimeMatrix()
        {
            const string character = "PrimordialGolemLifetime";
            var schedule = new AreaClockSchedule();
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var previousAreaUpdateContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = updates;
            ClientState? owner = null;
            ClientState? restored = null;
            ClientState? legacy = null;
            try
            {
                owner = new ClientState(new(1, "primordial-golem-lifetime", 1000, 1000, 100, 100, null, []), 4, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                owner.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    owner.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                owner.PlayerStats[Program.Stats.Key("minion_damage_+%")!.Value] = 50;
                owner.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;

                var inventory = global::Game.ItemsOf(owner);
                var jewelNode = Program.Passives.JewelNodeForSlot(0)!.Value;
                owner.Passives.Add(jewelNode);
                var normalMight = new UniqueCatalog(Program.Items).Create("Primordial Might", new Random(504))!;
                var replicaMight = new UniqueCatalog(Program.Items).Create("Replica Primordial Might", new Random(504))!;
                var chain = new UniqueCatalog(Program.Items).Create("The Primordial Chain", new Random(504))!;
                string ItemModId(ItemMod mod) => Program.Items.Mods.ByHash(mod.Hash)?.Id ?? "";
                var normalConditional = normalMight.Mods.Single(mod =>
                    ItemModId(mod) == "IncreasedGolemDamageIfGolemSummonedRecently__1_");
                var replicaConditional = replicaMight.Mods.Single(mod =>
                    ItemModId(mod) == "IncreasedGolemDamageIfGolemSummonedRecentlyUnique__1");
                int normalCoefficient = normalConditional.Values[0];
                int replicaCoefficient = replicaConditional.Values[0];
                Assert.InRange(normalCoefficient, 35, 45);
                Assert.InRange(replicaCoefficient, 100, 125);
                Assert.Contains(replicaMight.Mods, mod => ItemModId(mod) == "MaximumGolemsUnique__4_"
                    && mod.Values[0] == -1);
                Assert.Contains(replicaMight.Mods, mod => ItemModId(mod) == "GolemLifeUnique__1");
                Assert.Contains(chain.Mods, mod => ItemModId(mod) == "MaximumGolemsUnique__3"
                    && mod.Values[0] == 3);

                var normalWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                var stoneGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonRockGolem")!);
                var flameGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
                var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, stoneGem), new(SocketColour.White, flameGem)],
                    Links = [2],
                };
                inventory.Add(PlayerInventories.Weapon1, 0, 0, weapon);
                void Recalculate()
                {
                    global::Game.EquipmentStatSources(owner, inventory.EquipmentSnapshot());
                    owner.Pools = global::Game.PoolsFor(owner, 100);
                    owner.PlayerLife = owner.Pools.Life;
                    owner.PlayerMana = owner.Pools.Mana;
                }
                Recalculate();
                int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
                    stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
                GolemState.Ability Ability(GolemState.Golem golem, int effect) =>
                    golem.Abilities.Single(ability => ability.Effect == effect);
                GolemState.Ability ExpiredTwin(GolemState.Golem golem, IReadOnlyDictionary<uint, int> player,
                    double at, int effect)
                {
                    var twin = new GolemState.Golem(0, null!, golem.Definition, golem.Source)
                    {
                        Own = golem.Own, Player = new(player), SummonedAt = at - 8,
                    };
                    return global::Game.GolemAbilities(owner, global::Game.Monsters.Find(golem.Definition.Variety)!,
                        golem.Monster.Level, twin, player, at).Single(ability => ability.Effect == effect);
                }
                int ExpiredIncrease(GolemState.Golem golem, IReadOnlyDictionary<uint, int> player, double at)
                {
                    var twin = new GolemState.Golem(0, null!, golem.Definition, golem.Source)
                    {
                        Own = golem.Own, Player = new(player), SummonedAt = at - 8,
                    };
                    return global::Game.GolemModifiers(owner, twin, player, at).Effects["damage_+%"];
                }

                var stoneSource = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "SummonStoneGolem");
                var flameSource = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "SummonFlameGolem");
                var stoneDefinition = global::Game.GolemData.Find("SummonStoneGolem")!;
                var flameDefinition = global::Game.GolemData.Find("SummonFlameGolem")!;

                schedule.At = 6;
                var playerWithNormal = global::Game.CharacterPlayerStats(owner);
                Assert.True(Stat(playerWithNormal, "minion_damage_+%") >= 50);
                uint firstId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    stoneSource.Id, stoneDefinition, (100, 100));
                Assert.NotEqual(0u, firstId);
                var first = owner.Golems.All[firstId];
                Assert.Equal(6, first.SummonedAt);
                Assert.Equal(6, first.LastStep);
                var stoneSlam = Assert.Single(first.Abilities, ability =>
                    ability.Effect == 136 && ability.Attack.Damage.MaxTotal > 0);
                int stoneEffect = stoneSlam.Effect;
                Assert.Equal(normalCoefficient, global::Game.GolemRecentDamageIncrease(first, schedule.At));
                var normalAbility = Ability(first, stoneEffect);
                var normalExpiredAbility = ExpiredTwin(first, playerWithNormal, schedule.At, stoneEffect);
                int normalExpiredIncrease = ExpiredIncrease(first, playerWithNormal, schedule.At);
                double normalExpected = normalExpiredAbility.Attack.Damage.MinTotal
                    * (100 + normalExpiredIncrease + normalCoefficient) / (100.0 + normalExpiredIncrease);
                Assert.Equal(normalExpected, normalAbility.Attack.Damage.MinTotal, 3);

                var normalActiveAbility = Ability(first, stoneEffect);
                inventory.Remove(PlayerInventories.PassiveJewels, normalWorn.Id);
                Recalculate();
                schedule.At = 6.25;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(first, owner.Golems.All[firstId]);
                Assert.Equal(0, global::Game.GolemRecentDamageIncrease(first, schedule.At));
                var sourceOffAbility = Ability(first, stoneEffect);
                Assert.True(normalActiveAbility.Attack.Damage.MinTotal > sourceOffAbility.Attack.Damage.MinTotal);
                Assert.True(normalActiveAbility.Attack.Damage.MaxTotal > sourceOffAbility.Attack.Damage.MaxTotal);
                inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                Recalculate();
                schedule.At = 7;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(first, owner.Golems.All[firstId]);
                Assert.Equal(normalCoefficient, global::Game.GolemRecentDamageIncrease(first, schedule.At));
                var sourceRewornAbility = Ability(first, stoneEffect);
                Assert.Equal(normalActiveAbility.Attack.Damage.MinTotal, sourceRewornAbility.Attack.Damage.MinTotal);
                Assert.Equal(normalActiveAbility.Attack.Damage.MaxTotal, sourceRewornAbility.Attack.Damage.MaxTotal);

                schedule.At = 10;
                uint secondId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    stoneSource.Id, stoneDefinition, (100, 100));
                Assert.NotEqual(0u, secondId);
                var second = owner.Golems.All[secondId];
                Assert.Equal(2, owner.Golems.All.Count);

                var replacedNormal = inventory.Items(PlayerInventories.PassiveJewels).Single();
                inventory.Remove(PlayerInventories.PassiveJewels, replacedNormal.Id);
                inventory.Add(PlayerInventories.PassiveJewels, 0, 0, replicaMight);
                inventory.Add(5, 0, 0, chain);
                Recalculate();
                schedule.At = 10.25;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(first, owner.Golems.All[firstId]);
                Assert.Equal(replicaCoefficient, global::Game.GolemRecentDamageIncrease(first, schedule.At));
                var playerWithReplica = global::Game.CharacterPlayerStats(owner);
                var replicaAbility = Ability(first, stoneEffect);
                var replicaExpiredAbility = ExpiredTwin(first, playerWithReplica, schedule.At, stoneEffect);
                int replicaExpiredIncrease = ExpiredIncrease(first, playerWithReplica, schedule.At);
                double replicaExpected = replicaExpiredAbility.Attack.Damage.MinTotal
                    * (100 + replicaExpiredIncrease + replicaCoefficient) / (100.0 + replicaExpiredIncrease);
                Assert.Equal(replicaExpected, replicaAbility.Attack.Damage.MinTotal, 3);

                schedule.At = 10.5;
                uint flameId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    flameSource.Id, flameDefinition, (100, 100));
                Assert.NotEqual(0u, flameId);
                var flame = owner.Golems.All[flameId];
                Assert.Equal(3, owner.Golems.All.Count);
                Assert.Equal(replicaCoefficient, global::Game.GolemRecentDamageIncrease(flame, schedule.At));
                var flameSpell = flame.Abilities.First(ability => ability.Attack.Spell && ability.Attack.Damage.MaxTotal > 0);
                Assert.True(flameSpell.Attack.Damage.MinTotal > 0);

                schedule.At = 13.999;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(first, owner.Golems.All[firstId]);
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(first, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(second, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(owner, schedule.At));
                schedule.At = 14;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(first, owner.Golems.All[firstId]);
                Assert.Same(second, owner.Golems.All[secondId]);
                Assert.False(global::Game.GolemSummonedInPastEightSeconds(first, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(second, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(owner, schedule.At));
                Assert.Equal(14, first.LastStep);

                // Rebuild at a level change must preserve each actor's birth, and its initial native packet after restore.
                int firstBirth = (int)first.SummonedAt;
                int secondBirth = (int)second.SummonedAt;
                double flameBirth = flame.SummonedAt;
                schedule.At = 14.5;
                uint worn = inventory.Items(PlayerInventories.Weapon1).Single().Id;
                int levelTwenty = Program.Items.Gem(Program.Items.ByHash(stoneGem.TypeHash)!)!.ExperienceFor(20);
                inventory.Change(PlayerInventories.Weapon1, worn, item => item with
                {
                    Sockets =
                    [
                        new(SocketColour.White, stoneGem with { GemExperience = levelTwenty }),
                        new(SocketColour.White, flameGem),
                    ],
                });
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Equal(3, owner.Golems.All.Count);
                var rebuiltFirst = owner.Golems.All.Values.Single(golem =>
                    golem.Definition.Kind == "Stone" && (int)golem.SummonedAt == firstBirth);
                var rebuiltSecond = owner.Golems.All.Values.Single(golem =>
                    golem.Definition.Kind == "Stone" && (int)golem.SummonedAt == secondBirth);
                var rebuiltFlame = owner.Golems.All.Values.Single(golem =>
                    golem.Definition.Kind == "Flame" && golem.SummonedAt == flameBirth);
                Assert.Equal(70, rebuiltFirst.Monster.Level);
                Assert.Equal(14.5, rebuiltSecond.LastStep);
                Assert.Equal(replicaCoefficient, global::Game.GolemRecentDamageIncrease(rebuiltSecond, schedule.At));
                Assert.Equal(replicaCoefficient, global::Game.GolemRecentDamageIncrease(rebuiltFlame, schedule.At));
                Assert.Contains(rebuiltSecond.Skills, skill => skill.Stats?.Any() == true);

                // Save the partly aged actors, restore them, and prove the restored first step is not aged from birth.
                rebuiltSecond.Monster.Life = Math.Max(1, rebuiltSecond.Monster.MaxLife / 2);
                global::Game.SaveGolems(owner);
                var saved = JsonSerializer.Deserialize<GolemState.Saved[]>(
                    CharacterSaves.ModuleData(character, "Game.Golems")!)!;
                Assert.All(saved.Where(entry => entry.ReviveSeconds == 0),
                    entry => Assert.True(double.IsFinite(entry.SummonedSecondsRemaining)));
                Assert.Contains(saved, entry => entry.SummonedSecondsRemaining is > 3 and < 5);
                double restoreAt = schedule.At;

                restored = new ClientState(new(1, "primordial-golem-restored", 1000, 1000, 100, 100, null, []), 5, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                restored.Passives.Add(jewelNode);
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    restored.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                restored.PlayerStats[Program.Stats.Key("minion_damage_+%")!.Value] = 50;
                restored.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                global::Game.EquipmentStatSources(restored, inventory.EquipmentSnapshot());
                restored.Pools = global::Game.PoolsFor(restored, 100);
                restored.PlayerLife = restored.Pools.Life; restored.PlayerMana = restored.Pools.Mana;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, restored, restoreAt);
                Assert.Equal(3, restored.Golems.All.Count);
                var restoredSecond = restored.Golems.All.Values.Single(golem =>
                    golem.Definition.Kind == "Stone" && (int)golem.SummonedAt == secondBirth);
                var restoredFlame = restored.Golems.All.Values.Single(golem =>
                    golem.Definition.Kind == "Flame" && golem.SummonedAt == flameBirth);
                Assert.Equal(restoreAt, restoredSecond.LastStep);
                Assert.True(restoredSecond.SummonedAt < restoredSecond.LastStep);
                Assert.Equal(replicaCoefficient,
                    global::Game.GolemRecentDamageIncrease(restoredSecond, restoreAt));
                Assert.Equal(rebuiltSecond.Monster.Life / (double)rebuiltSecond.Monster.MaxLife,
                    restoredSecond.Monster.Life / (double)restoredSecond.Monster.MaxLife, 2);
                Assert.NotEmpty(restoredSecond.Skills);
                uint restoredLife = restoredSecond.Monster.Life;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, restored, restoreAt);
                Assert.Equal(restoredLife, restoredSecond.Monster.Life);
                Assert.Equal(restoreAt, restoredSecond.LastStep);

                schedule.At = 18;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(rebuiltFirst, owner.Golems.All[rebuiltFirst.Id]);
                Assert.Same(rebuiltSecond, owner.Golems.All[rebuiltSecond.Id]);
                Assert.False(global::Game.GolemSummonedInPastEightSeconds(rebuiltSecond, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(rebuiltFlame, schedule.At));
                Assert.True(global::Game.GolemSummonedInPastEightSeconds(owner, schedule.At));

                schedule.At = 18.499;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                var staleFlameAbility = rebuiltFlame.Abilities.Single(ability => ability.Effect == flameSpell.Effect);
                Assert.True(global::Game.GolemRecentDamageIncrease(rebuiltFlame, schedule.At) > 0);
                schedule.At = 18.5;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                var currentFlameAbility = rebuiltFlame.Abilities.Single(ability => ability.Effect == staleFlameAbility.Effect);
                Assert.Equal(0, global::Game.GolemRecentDamageIncrease(rebuiltFlame, schedule.At));
                Assert.True(staleFlameAbility.Attack.Damage.MinTotal > currentFlameAbility.Attack.Damage.MinTotal);
                Assert.True(staleFlameAbility.Attack.Damage.MaxTotal > currentFlameAbility.Attack.Damage.MaxTotal);

                const uint targetId = 0x08000003;
                var from = rebuiltFlame.Monster.Walk.Position(schedule.At);
                var target = new SpawnedMonster("primordial golem target",
                    new(from.X + 2, from.Y, 0), 100, 8, 1_000_000_000);
                owner.Monsters[targetId] = target;
                int outputStart = owner.Minions.OutputQueue.Count;
                uint targetLife = target.Life;
                try
                {
                    Assert.True(global::Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, owner, rebuiltFlame,
                        staleFlameAbility, targetId, target, schedule.At));
                    Assert.True(target.Life < targetLife);
                    int lost = (int)(targetLife - target.Life);
                    var hitOutput = owner.Minions.OutputQueue.Skip(outputStart).Single(output =>
                        output.Packet.Length >= 2
                        && BinaryPrimitives.ReadUInt16BigEndian(output.Packet) == InstanceProtocol.ActionHitId);
                    ushort hitFlags = BinaryPrimitives.ReadUInt16BigEndian(hitOutput.Packet.AsSpan(20));
                    bool criticalHit = (hitFlags & 0x0020) != 0;
                    double criticalFactor = 1;
                    if (criticalHit && !target.Effects.NoExtraCriticalDamage)
                    {
                        var effects = rebuiltFlame.Monster.Mods.Effects;
                        criticalFactor = 1.3 + (effects["base_critical_strike_multiplier_+"]
                            + effects["critical_strike_multiplier_+"]
                            + target.Curses["enemy_additional_critical_strike_multiplier_against_self"]) / 100.0;
                    }
                    var possibleDamage = currentFlameAbility.Attack.Damage.Scale(criticalFactor);
                    Assert.InRange(lost, (int)Math.Floor(possibleDamage.MinTotal),
                        (int)Math.Ceiling(possibleDamage.MaxTotal));
                    Assert.Equal(global::Game.HitCode(currentFlameAbility.Attack.Damage), hitOutput.Packet[23]);

                    var currentAbilities = rebuiltFlame.Abilities;
                    uint targetLifeBeforeMissing = target.Life;
                    int missingOutputStart = owner.Minions.OutputQueue.Count;
                    rebuiltFlame.Abilities = currentAbilities
                        .Where(ability => ability.Effect != staleFlameAbility.Effect).ToArray();
                    Assert.True(owner.Monsters.TryGetValue(targetId, out var registeredTarget));
                    Assert.Same(target, registeredTarget);
                    Assert.False(target.Dead);
                    Assert.True(target.Life > 0);
                    Assert.False(target.Invulnerable);
                    Assert.True(target.Hostile);
                    Assert.False(global::Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, owner, rebuiltFlame,
                        staleFlameAbility, targetId, target, schedule.At + .001));
                    Assert.Equal(targetLifeBeforeMissing, target.Life);
                    Assert.DoesNotContain(owner.Minions.OutputQueue.Skip(missingOutputStart), output =>
                        output.Packet.Length >= 2
                        && BinaryPrimitives.ReadUInt16BigEndian(output.Packet) == InstanceProtocol.ActionHitId);

                    rebuiltFlame.Abilities = currentAbilities;
                    uint targetLifeBeforeRestored = target.Life;
                    int restoredOutputStart = owner.Minions.OutputQueue.Count;
                    Assert.True(global::Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, owner, rebuiltFlame,
                        staleFlameAbility, targetId, target, schedule.At + .001));
                    Assert.True(target.Life < targetLifeBeforeRestored);
                    var restoredHit = owner.Minions.OutputQueue.Skip(restoredOutputStart).Single(output =>
                        output.Packet.Length >= 2
                        && BinaryPrimitives.ReadUInt16BigEndian(output.Packet) == InstanceProtocol.ActionHitId);
                    Assert.Equal(global::Game.HitCode(currentFlameAbility.Attack.Damage), restoredHit.Packet[23]);
                }
                finally
                {
                    owner.Monsters.Remove(targetId);
                }

                // The generic-fighter definition is the only switch that grants the golem-only conditional.
                uint golemRecentDamage = Program.Stats.Key("golem_damage_+%_if_summoned_in_past_8_seconds")!.Value;
                var playerWithRecentDamage = new Dictionary<uint, int>(rebuiltFlame.Player);
                Assert.True(playerWithRecentDamage.GetValueOrDefault(golemRecentDamage) > 0);
                var playerWithoutRecentDamage = new Dictionary<uint, int>(playerWithRecentDamage);
                playerWithoutRecentDamage.Remove(golemRecentDamage);
                var generic = new GolemState.Golem(0, null!, rebuiltFlame.Definition with { IsGolem = false },
                    rebuiltFlame.Source)
                {
                    Own = rebuiltFlame.Own, Player = new(playerWithRecentDamage),
                    SummonedAt = 18.25,
                };
                Assert.Equal(0, global::Game.GolemRecentDamageIncrease(generic, 18.5));
                double ProjectedDamage(GolemState.Golem golem, IReadOnlyDictionary<uint, int> player)
                {
                    var ability = global::Game.GolemAbilities(owner,
                        global::Game.Monsters.Find(golem.Definition.Variety)!, golem.Monster?.Level ?? 70,
                        golem, player, 18.25).Single(candidate => candidate.Effect == staleFlameAbility.Effect);
                    Assert.True(ability.Attack.Damage.MaxTotal > 0);
                    return ability.Attack.Damage.MinTotal;
                }
                Assert.Equal(ProjectedDamage(generic, playerWithoutRecentDamage),
                    ProjectedDamage(generic, playerWithRecentDamage));
                Assert.True(ProjectedDamage(rebuiltFlame, playerWithRecentDamage)
                    > ProjectedDamage(rebuiltFlame, playerWithoutRecentDamage));

                // An old save without the optional field is expired at its first raw native and ability build.
                var legacyEntries = saved.Select(entry => new
                {
                    entry.Effect, entry.Skill, entry.Life, entry.ReviveSeconds,
                }).ToArray();
                CharacterSaves.ChangeModuleData(character, "Game.Golems", _ => JsonSerializer.Serialize(legacyEntries));
                schedule.At = 19;
                legacy = new ClientState(new(1, "primordial-golem-legacy", 1000, 1000, 100, 100, null, []), 6, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                legacy.Passives.Add(jewelNode);
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    legacy.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                legacy.PlayerStats[Program.Stats.Key("minion_damage_+%")!.Value] = 50;
                legacy.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                global::Game.EquipmentStatSources(legacy, inventory.EquipmentSnapshot());
                legacy.Pools = global::Game.PoolsFor(legacy, 100);
                legacy.PlayerLife = legacy.Pools.Life; legacy.PlayerMana = legacy.Pools.Mana;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, legacy, schedule.At);
                Assert.Equal(3, legacy.Golems.All.Count);
                Assert.All(legacy.Golems.All.Values, golem =>
                {
                    Assert.Equal(11, golem.SummonedAt);
                    Assert.Equal(19, golem.LastStep);
                    Assert.Equal(0, global::Game.GolemRecentDamageIncrease(golem, schedule.At));
                });
                Assert.All(legacy.Golems.All.Values, golem => Assert.NotEmpty(golem.Skills));

                foreach (double invalidRemaining in new[] { double.NaN, double.PositiveInfinity })
                {
                    schedule.At++;
                    uint invalidId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, legacy,
                        stoneSource.Id, stoneDefinition, (100, 100), arrival: true,
                        summonedSecondsRemaining: invalidRemaining);
                    Assert.NotEqual(0u, invalidId);
                    var invalid = legacy.Golems.All[invalidId];
                    Assert.Equal(schedule.At - 8, invalid.SummonedAt);
                    Assert.Equal(schedule.At, invalid.LastStep);
                    Assert.Equal(0, global::Game.GolemRecentDamageIncrease(invalid, schedule.At));
                }
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousAreaUpdateContext;
                foreach (var client in new[] { legacy, restored, owner })
                    if (client is not null) client.Closed = true;
                PlayerInventories.Forget(character);
                global::Game.InactiveItems.TryRemove(character, out _);
            }
        }

        async Task PrimordialGroundHazardWindow()
        {
            const string character = "PrimordialGroundHazard";
            const string condition = "golem_damage_+%_if_summoned_in_past_8_seconds";
            const string minionDamage = "minion_damage_+%";
            const string numberAllowed = "number_of_golems_allowed";
            var schedule = new AreaClockSchedule();
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var previousAreaUpdateContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = updates;
            ClientState? owner = null;
            try
            {
                owner = new ClientState(new(1, "primordial-ground-hazard", 1000, 1000, 100, 100, null, []), 7, 1)
                {
                    Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                owner.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    owner.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                owner.PlayerStats[Program.Stats.Key(minionDamage)!.Value] = 50;

                var inventory = global::Game.ItemsOf(owner);
                var jewelNode = Program.Passives.JewelNodeForSlot(0)!.Value;
                owner.Passives.Add(jewelNode);
                var normalMight = new UniqueCatalog(Program.Items).Create("Primordial Might", new Random(504))!;
                var replicaMight = new UniqueCatalog(Program.Items).Create("Replica Primordial Might", new Random(504))!;
                string ItemModId(ItemMod mod) => Program.Items.Mods.ByHash(mod.Hash)?.Id ?? "";
                int normalCoefficient = normalMight.Mods.Single(mod =>
                    ItemModId(mod) == "IncreasedGolemDamageIfGolemSummonedRecently__1_").Values[0];
                int replicaCoefficient = replicaMight.Mods.Single(mod =>
                    ItemModId(mod) == "IncreasedGolemDamageIfGolemSummonedRecentlyUnique__1").Values[0];
                Assert.InRange(normalCoefficient, 35, 45);
                Assert.InRange(replicaCoefficient, 100, 125);
                Assert.Contains(replicaMight.Mods, mod => ItemModId(mod) == "MaximumGolemsUnique__4_"
                    && mod.Values[0] == -1);

                var chaosGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonChaosGolem")!);
                var baseFlameType = Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!;
                var flameDefinition = global::Game.GolemData.Find("SummonFlameGolemAltY")!;
                Assert.Equal(4765, flameDefinition.Effect);
                var meteorVariant = Program.Items.Gem(baseFlameType)!.Variants
                    .Single(variant => variant.Effect == flameDefinition.Effect);
                var meteorGem = ItemCatalog.Create(baseFlameType) with { GemVariant = meteorVariant.Index };
                var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, chaosGem), new(SocketColour.White, meteorGem)],
                    Links = [2],
                };
                inventory.Add(PlayerInventories.Weapon1, 0, 0, weapon);
                void Recalculate()
                {
                    global::Game.EquipmentStatSources(owner, inventory.EquipmentSnapshot());
                    owner.Pools = global::Game.PoolsFor(owner, 100);
                    owner.PlayerLife = owner.Pools.Life;
                    owner.PlayerMana = owner.Pools.Mana;
                }
                Recalculate();

                var chaosSource = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "SummonChaosGolem");
                var chaosDefinition = global::Game.GolemData.Find("SummonChaosGolem")!;
                schedule.At = 6;
                uint chaosId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    chaosSource.Id, chaosDefinition, (100, 100));
                Assert.NotEqual(0u, chaosId);
                GolemState.Golem CurrentGolem() => Assert.Single(owner!.Golems.All).Value;
                var chaos = CurrentGolem();
                Assert.Equal(6, chaos.SummonedAt);
                Assert.False(chaos.Monster.Dead);
                var aura = Assert.Single(chaos.Abilities, ability => ability.Effect == 4336);
                int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
                    Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
                double MoreFactor(int percent) => Math.Max(0, 1 + percent / 100.0);
                double UnitDamage(IReadOnlyDictionary<uint, int> stats, GolemState.Golem golem, string perMinute)
                {
                    Assert.Equal(0, Stat(golem.Own, "support_minion_damage_+%_final"));
                    Assert.Equal(0, Stat(golem.Player, "golem_damage_+%_final_from_unique_primordial_tether"));
                    Assert.Equal(0, Stat(golem.Player, "unique_primordial_tether_golem_damage_+%_final"));
                    return Stat(stats, perMinute) / 60.0
                        * MoreFactor(Stat(golem.Own, "active_skill_minion_damage_+%_final"));
                }
                double ChaosUnitDamage(GolemState.Golem golem, GolemState.Ability ability) =>
                    UnitDamage(ability.Stats, golem, "base_chaos_damage_to_deal_per_minute");
                double FireUnitDamage(GolemState.Golem golem, GolemState.Ability ability) =>
                    UnitDamage(ability.Stats, golem, "base_fire_damage_to_deal_per_minute");
                var chaosUnitDps = ChaosUnitDamage(chaos, aura);
                Assert.True(chaosUnitDps > 0);

                int NextTargetId = 0x08000100;
                SpawnedMonster AddTarget(string name, GolemState.Golem golem, double at, out uint targetId)
                {
                    targetId = (uint)NextTargetId++;
                    var position = golem.Monster.Walk.Position(at);
                    var target = new SpawnedMonster(name, new(position.X, position.Y, 0), 100, 8, 1_000_000_000)
                    { Hostile = true };
                    owner!.Monsters[targetId] = target;
                    return target;
                }
                void ConsumeRaw(GolemState.Golem golem, GolemState.Hazard activeHazard, double at, double expectedRaw,
                    SpawnedMonster target, uint targetId, string stage)
                {
                    Assert.Equal(0, global::Game.MonsterHitResistance(target, activeHazard.Type, default).Value);
                    Assert.Equal(1.0, global::Game.MonsterDamageTaken(target, spell: true, now: at,
                        chaos: activeHazard.Type == DamageType.Chaos), 12);
                    double total = expectedRaw + activeHazard.DamageCarry.GetValueOrDefault(targetId);
                    int expectedDamage = (int)Math.Floor(total);
                    double expectedCarry = total - expectedDamage;
                    uint before = target.Life;
                    global::Game.GolemHazardsLocked("test", wire.Stream, wire.Ciphers, owner!, golem, at);
                    uint lost = before - target.Life;
                    Assert.True(lost == expectedDamage,
                        $"{stage}: expected {expectedDamage} life loss, actual {lost}; raw={expectedRaw:R}");
                    Assert.Equal(expectedCarry, activeHazard.DamageCarry.GetValueOrDefault(targetId), 12);
                    Assert.Equal(Math.Min(at, activeHazard.Until), activeHazard.LastAt, 12);
                    owner!.Monsters.Remove(targetId);
                }
                void ConsumeKnown50(GolemState.Golem golem, GolemState.Hazard activeHazard, double at, double unitDps,
                    double elapsed, double activeSeconds, int coefficient, string stage)
                {
                    Assert.True(coefficient >= 0);
                    Assert.InRange(activeSeconds, 0, elapsed);
                    double expectedRaw = unitDps * (1.5 * elapsed + coefficient / 100.0 * activeSeconds);
                    var target = AddTarget(stage, golem, at, out uint targetId);
                    ConsumeRaw(golem, activeHazard, at, expectedRaw, target, targetId, stage);
                }
                void ConsumeZeroBaseline(GolemState.Golem golem, GolemState.Hazard activeHazard, double at, double unitDps,
                    double elapsed, double activeSeconds, int coefficient, string stage)
                {
                    Assert.Equal(0, activeHazard.DamagePerSecond);
                    Assert.InRange(activeSeconds, 0, elapsed);
                    double expectedRaw = unitDps * Math.Max(0, 1 + (-100 + coefficient) / 100.0) * activeSeconds;
                    var target = AddTarget(stage, golem, at, out uint targetId);
                    ConsumeRaw(golem, activeHazard, at, expectedRaw, target, targetId, stage);
                }

                void Contact(GolemState.Golem golem, GolemState.Ability ability, double at)
                {
                    var action = new GolemState.Action(ability, 0, null, golem.Monster.Walk.Position(at), at,
                        golem.Monster.ActionGeneration);
                    global::Game.GolemAbilityContactLocked("test", wire.Stream, wire.Ciphers, owner!,
                        golem, action, at);
                }

                schedule.At = 12;
                Contact(chaos, aura, schedule.At);
                var hazard = Assert.Single(chaos.Hazards, candidate => candidate.Effect == 4336);
                Assert.True(hazard.Until > 15);
                Assert.Equal(12, hazard.LastAt);
                Assert.Equal(50, hazard.NormalDamageIncrease);
                Assert.Equal(chaosUnitDps, hazard.UnitDamagePerSecond, 12);
                Assert.Equal(chaosUnitDps * 1.5, hazard.DamagePerSecond, 12);

                var normalWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                Recalculate();
                schedule.At = 12;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(chaos, CurrentGolem());
                Assert.Equal(normalCoefficient, chaos.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                Assert.DoesNotContain(owner.Monsters.Values, monster => monster.Hostile);

                schedule.At = 13;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: 1, activeSeconds: 1,
                    normalCoefficient, "before eight seconds");

                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: 0, activeSeconds: 0,
                    normalCoefficient, "repeated same clock");

                schedule.At = 15;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: 2, activeSeconds: 1,
                    normalCoefficient, "delayed crossing eight seconds");

                double afterBoundarySeconds = hazard.Until - 15;
                schedule.At = hazard.Until;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, afterBoundarySeconds, activeSeconds: 0,
                    normalCoefficient, "after eight seconds to hazard cap");
                Assert.DoesNotContain(chaos.Hazards, candidate => ReferenceEquals(candidate, hazard));
                schedule.At = hazard.Until + 1;
                global::Game.GolemHazardsLocked("test", wire.Stream, wire.Ciphers, owner, chaos, schedule.At);
                Assert.Empty(chaos.Hazards);

                inventory.Remove(PlayerInventories.PassiveJewels, normalWorn.Id);
                Recalculate();
                schedule.At = 30;
                uint replacementId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    chaosSource.Id, chaosDefinition, (100, 100));
                Assert.NotEqual(0u, replacementId);
                chaos = owner.Golems.All[replacementId];
                Assert.Equal(30, chaos.SummonedAt);
                aura = Assert.Single(chaos.Abilities, ability => ability.Effect == 4336);
                chaosUnitDps = ChaosUnitDamage(chaos, aura);
                Contact(chaos, aura, schedule.At);
                hazard = Assert.Single(chaos.Hazards, candidate => candidate.Effect == 4336);
                Assert.Equal(0, chaos.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                Assert.Equal(chaosUnitDps * 1.5, hazard.DamagePerSecond, 12);

                normalWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                Recalculate();
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(chaos, owner.Golems.All[replacementId]);
                Assert.Equal(normalCoefficient, chaos.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                schedule.At = 30.5;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: .5, activeSeconds: .5,
                    normalCoefficient, "normal source added to existing hazard");

                inventory.Remove(PlayerInventories.PassiveJewels, normalWorn.Id);
                owner.PlayerStats[Program.Stats.Key(numberAllowed)!.Value] = 1;
                var replicaWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, replicaMight);
                Recalculate();
                schedule.At = 31;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(chaos, owner.Golems.All[replacementId]);
                Assert.Equal(replicaCoefficient, chaos.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                Assert.True(chaos.Limit > 0);
                schedule.At = 31.5;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: .5, activeSeconds: .5,
                    replicaCoefficient, "actual replica source on same young golem and hazard");
                var replicaPlayer = new Dictionary<uint, int>(chaos.Player);

                inventory.Remove(PlayerInventories.PassiveJewels, replicaWorn.Id);
                Recalculate();
                schedule.At = 32;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(chaos, owner.Golems.All[replacementId]);
                Assert.Equal(0, chaos.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                schedule.At = 33;
                ConsumeKnown50(chaos, hazard, schedule.At, chaosUnitDps, elapsed: 1, activeSeconds: 0,
                    coefficient: 0, "source removed returns to baseline");

                var generic = new GolemState.Golem(chaos.Id, chaos.Monster,
                    chaos.Definition with { IsGolem = false }, chaos.Source)
                {
                    Player = replicaPlayer,
                    SummonedAt = chaos.SummonedAt,
                };
                generic.Hazards.Add(new(4336, generic.Monster.Walk.Position(33.5), 34, 10, chaosUnitDps * 1.5,
                    DamageType.Chaos, UnitDamagePerSecond: chaosUnitDps, NormalDamageIncrease: 50) { LastAt = 33.5 });
                var genericHazard = Assert.Single(generic.Hazards);
                schedule.At = 34;
                var genericTarget = AddTarget("generic baseline target", generic, schedule.At, out uint genericTargetId);
                ConsumeRaw(generic, genericHazard, schedule.At,
                    chaosUnitDps * 1.5 * .5, genericTarget, genericTargetId,
                    "generic definition ignores conditional source");

                var legacy = new GolemState.Golem(chaos.Id, chaos.Monster, chaos.Definition, chaos.Source)
                {
                    Player = replicaPlayer,
                    SummonedAt = chaos.SummonedAt,
                };
                legacy.Hazards.Add(new(4336, legacy.Monster.Walk.Position(34.5), 35, 10, chaosUnitDps * 1.5,
                    DamageType.Chaos) { LastAt = 34.5 });
                var legacyHazard = Assert.Single(legacy.Hazards);
                schedule.At = 35;
                var legacyTarget = AddTarget("legacy baseline target", legacy, schedule.At, out uint legacyTargetId);
                ConsumeRaw(legacy, legacyHazard, schedule.At,
                    chaosUnitDps * 1.5 * .5, legacyTarget, legacyTargetId,
                    "default hazard metadata keeps its positive baseline");

                var meteorSource = Assert.Single(global::Game.GemSkillEffects(owner),
                    grant => global::Game.SkillsFor(owner).EffectId(grant.Effect) == "SummonFlameGolemAltY");
                schedule.At = 40;
                uint meteorId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    meteorSource.Id, flameDefinition, (100, 100));
                Assert.NotEqual(0u, meteorId);
                var meteorGolem = owner.Golems.All[meteorId];
                Assert.Equal(flameDefinition.Id, meteorGolem.Definition.Id);
                Assert.True(meteorGolem.Definition.Special);
                Assert.NotNull(meteorGolem.Pending);
                Assert.Equal(5268, meteorGolem.Pending.Ability.Effect);
                normalWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                Recalculate();
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(meteorGolem, owner.Golems.All[meteorId]);
                Assert.Equal(normalCoefficient,
                    meteorGolem.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                schedule.At = 41;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Same(meteorGolem, owner.Golems.All[meteorId]);
                var meteorHazard = Assert.Single(meteorGolem.Hazards, candidate => candidate.Effect == 5268);
                var meteorAbility = Assert.Single(meteorGolem.Abilities, ability => ability.Effect == 5268);
                double meteorUnitDps = FireUnitDamage(meteorGolem, meteorAbility);
                Assert.Equal(DamageType.Fire, meteorHazard.Type);
                Assert.True(meteorUnitDps > 0);
                Assert.Equal(meteorUnitDps, meteorHazard.UnitDamagePerSecond, 12);
                Assert.Equal(meteorUnitDps * 1.5, meteorHazard.DamagePerSecond, 12);
                Assert.Equal(50, meteorHazard.NormalDamageIncrease);
                Assert.Equal(41, meteorHazard.LastAt);
                Assert.Equal(46, meteorHazard.Until);
                schedule.At = 41.5;
                ConsumeKnown50(meteorGolem, meteorHazard, schedule.At, meteorUnitDps, elapsed: .5, activeSeconds: .5,
                    normalCoefficient, "actual special meteor hazard");

                inventory.Remove(PlayerInventories.PassiveJewels, normalWorn.Id);
                Recalculate();
                schedule.At = 42;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Equal(0, meteorGolem.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                schedule.At = 42.5;
                Assert.Single(meteorGolem.Hazards);
                ConsumeKnown50(meteorGolem, meteorHazard, schedule.At, meteorUnitDps, elapsed: .5, activeSeconds: 0,
                    coefficient: 0, "meteor source removed returns to baseline");

                double meteorAfterBoundary = meteorHazard.Until - 42.5;
                schedule.At = meteorHazard.Until;
                ConsumeKnown50(meteorGolem, meteorHazard, schedule.At, meteorUnitDps, meteorAfterBoundary,
                    activeSeconds: 0, coefficient: 0, "meteor hazard cap");
                Assert.DoesNotContain(meteorGolem.Hazards, candidate => ReferenceEquals(candidate, meteorHazard));

                // A current actor can retain its last young snapshot. Contact without a corrective GolemStep must
                // synchronize the modifier before it captures the expired hazard's numeric metadata.
                schedule.At = 50;
                uint expiredId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    meteorSource.Id, flameDefinition, (100, 100));
                Assert.NotEqual(0u, expiredId);
                var expiredMeteor = owner.Golems.All[expiredId];
                normalWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, normalMight);
                Recalculate();
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Equal(normalCoefficient, expiredMeteor.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                Assert.Equal(50 + normalCoefficient, expiredMeteor.Monster.Mods.Effects["damage_+%"]);
                var expiredAbility = Assert.Single(expiredMeteor.Abilities, ability => ability.Effect == 5268);
                schedule.At = 59;
                Contact(expiredMeteor, expiredAbility, schedule.At);
                var expiredHazard = Assert.Single(expiredMeteor.Hazards, candidate => candidate.Effect == 5268);
                double expiredUnitDps = FireUnitDamage(expiredMeteor, expiredAbility);
                Assert.Equal(0, global::Game.GolemRecentDamageIncrease(expiredMeteor, schedule.At));
                Assert.Equal(50, expiredHazard.NormalDamageIncrease);
                Assert.Equal(expiredUnitDps, expiredHazard.UnitDamagePerSecond, 12);
                Assert.Equal(expiredUnitDps * 1.5, expiredHazard.DamagePerSecond, 12);
                schedule.At = 59.5;
                ConsumeKnown50(expiredMeteor, expiredHazard, schedule.At, expiredUnitDps, elapsed: .5, activeSeconds: 0,
                    normalCoefficient, "expired creation excludes stale conditional source");
                inventory.Remove(PlayerInventories.PassiveJewels, normalWorn.Id);
                Recalculate();

                // A true golem's unconditional unit rate keeps a zero-baseline meteor alive. Equipping the real
                // Replica source after creation gives that same young hazard positive conditional damage.
                owner.PlayerStats[Program.Stats.Key(minionDamage)!.Value] = -100;
                Recalculate();
                schedule.At = 70;
                uint zeroId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, owner,
                    meteorSource.Id, flameDefinition, (100, 100));
                Assert.NotEqual(0u, zeroId);
                var zeroMeteor = owner.Golems.All[zeroId];
                var zeroAbility = Assert.Single(zeroMeteor.Abilities, ability => ability.Effect == 5268);
                Contact(zeroMeteor, zeroAbility, schedule.At);
                var zeroHazard = Assert.Single(zeroMeteor.Hazards, candidate => candidate.Effect == 5268);
                double zeroUnitDps = FireUnitDamage(zeroMeteor, zeroAbility);
                Assert.True(zeroUnitDps > 0);
                Assert.Equal(-100, zeroHazard.NormalDamageIncrease);
                Assert.Equal(zeroUnitDps, zeroHazard.UnitDamagePerSecond, 12);
                Assert.Equal(0, zeroHazard.DamagePerSecond);

                replicaWorn = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, replicaMight);
                Recalculate();
                schedule.At = 70.5;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, owner, schedule.At);
                Assert.Equal(replicaCoefficient, zeroMeteor.Player.GetValueOrDefault(Program.Stats.Key(condition)!.Value));
                schedule.At = 71;
                ConsumeZeroBaseline(zeroMeteor, zeroHazard, schedule.At, zeroUnitDps, elapsed: .5, activeSeconds: .5,
                    replicaCoefficient, "zero-baseline meteor gains real conditional source");
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousAreaUpdateContext;
                if (owner is not null)
                {
                    owner.Closed = true;
                    PlayerInventories.Forget(character);
                    global::Game.InactiveItems.TryRemove(character, out _);
                }
            }
        }

        try
        {
            await PrimordialMightOwnerWindow();
            await PrimordialHarmonySourceConsumers();
            await PrimordialGolemLifetimeMatrix();
            await PrimordialGroundHazardWindow();
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
            var weapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
            { Sockets = [new(SocketColour.White, gem)] };
            Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, weapon);
            var source = Game.GemSkillEffects(conn).Single(g => g.Name == "Summon Flame Golem");
            var definition = Game.GolemData.Find("SummonFlameGolem")!;
            uint first = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, conn, source.Id, definition, (100, 100));
            Assert.NotEqual(0u, first);
            uint next = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, conn, source.Id, definition, (100, 100));
            Assert.NotEqual(first, next); Assert.Single(conn.Golems.All); Assert.False(conn.Monsters.ContainsKey(first));
            var golem = conn.Golems.All[next];
            var baselineAttacks = golem.Abilities.Where(a => !a.Attack.Spell).ToDictionary(a => a.Effect, a => a.Attack.Seconds);
            var baselineSpells = golem.Abilities.Where(a => a.Attack.Spell).ToDictionary(a => a.Effect, a => a.Attack.Seconds);
            var baselineMovement = golem.Monster.Mods.Effects.MovementPct;
            uint maximumEnergyShield = Program.Stats.Key("maximum_energy_shield")!.Value;
            Assert.Equal(249u, maximumEnergyShield);
            var plainMonster = new MonsterObject(100, 100, 1234, MonsterObject.BaseComponents);
            byte[] PlainPool(uint current)
            {
                var pool = new byte[18];
                BinaryPrimitives.WriteUInt32LittleEndian(pool, current);
                return pool;
            }
            Assert.True(plainMonster.Blob().AsSpan().IndexOf(
                [.. PlainPool(1234), .. PlainPool(0), .. PlainPool(0)]) >= 0);
            Assert.Null(plainMonster.Stats);
            Assert.Equal(0u, conn.MinionOrigins[next].Object.EnergyShield);
            Assert.DoesNotContain(conn.MinionOrigins[next].Object.Stats ?? [], stat => stat.Key == maximumEnergyShield);

            // The real unique's two coefficient rows use the owner's final Dexterity in complete groups of 50.
            // Keep the raw base deliberately different from the final value to catch a base_dexterity consumer.
            var unique = new UniqueCatalog(Program.Items).Create("Speaker's Wreath", new Random(504))!;
            string ItemModId(ItemMod mod) => Program.Items.Mods.ByHash(mod.Hash)?.Id ?? "";
            Assert.Contains("DexterityUnique__20__", unique.Mods.Select(ItemModId));
            Assert.Contains("MinionAttackSpeedPerXDexUnique__1", unique.Mods.Select(ItemModId));
            Assert.Contains("MinionMovementSpeedPerXDexUnique__1", unique.Mods.Select(ItemModId));
            int rolledDexterity = unique.Mods.Where(mod => ItemModId(mod) == "DexterityUnique__20__").Sum(mod => mod.Values[0]);
            var inventory = Game.ItemsOf(conn);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            int beforeSpeakerDexterity = conn.Pools.Dexterity;
            foreach (var old in inventory.Items(PlayerInventories.Helmet)) inventory.Remove(PlayerInventories.Helmet, old.Id);
            inventory.Add(PlayerInventories.Helmet, 0, 0, unique);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            Assert.Equal(beforeSpeakerDexterity + rolledDexterity, conn.Pools.Dexterity);

            // The same shared attribute resolver includes typed/all-attribute percentages before this consumer.
            int withoutPercentDexterity = conn.Pools.Dexterity;
            conn.PlayerStats[Program.Stats.Key("dexterity_+%")!.Value] = 100;
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            Assert.Equal(withoutPercentDexterity * 2, conn.Pools.Dexterity);
            conn.PlayerStats.Remove(Program.Stats.Key("dexterity_+%")!.Value);
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);

            var finalDexterity = Program.Stats.Key("dexterity")!.Value;
            var baseDexterity = Program.Stats.Key("base_dexterity")!.Value;
            conn.PlayerStats[baseDexterity] = 1000;
            void SetFinalDexterity(int value) => conn.PlayerStats[finalDexterity] = value;
            async Task RefreshGolemAt(double at, int dexterity, int expectedBonus)
            {
                SetFinalDexterity(dexterity);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, at);
                Assert.Equal(baselineMovement + expectedBonus, golem.Monster.Mods.Effects.MovementPct);
                foreach (var ability in golem.Abilities.Where(a => !a.Attack.Spell))
                    Assert.Equal(baselineAttacks[ability.Effect] / (1 + expectedBonus / 100.0), ability.Attack.Seconds, 6);
                foreach (var ability in golem.Abilities.Where(a => a.Attack.Spell))
                    Assert.Equal(baselineSpells[ability.Effect], ability.Attack.Seconds, 6);
            }

            await RefreshGolemAt(1, 49, 0);
            await RefreshGolemAt(1.6, 50, 2);
            await RefreshGolemAt(2.2, 99, 2);
            var sent = new List<byte[]>();
            var previousOutgoing = Program.Outgoing;
            Program.Outgoing = (ciphers, bytes) =>
            {
                if (ReferenceEquals(ciphers, wire.Ciphers)) lock (sent) sent.Add(bytes.ToArray());
                previousOutgoing?.Invoke(ciphers, bytes);
            };
            try { await RefreshGolemAt(2.8, 100, 4); }
            finally { Program.Outgoing = previousOutgoing; }
            Assert.Contains(sent, packet => packet.SequenceEqual(InstanceEntities.StatsUpdate(next,
                [(StatKeys.MovementVelocityPct, baselineMovement + 4)])));

            // A second refresh at the same concrete inputs rebuilds the buckets instead of adding the coefficient again.
            SetFinalDexterity(100);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 3.4);
            Assert.Equal(baselineMovement + 4, golem.Monster.Mods.Effects.MovementPct);
            foreach (var ability in golem.Abilities.Where(a => !a.Attack.Spell))
                Assert.Equal(baselineAttacks[ability.Effect] / 1.04, ability.Attack.Seconds, 6);

            // Summons use the same modifier projection and refresh path: a real Raise Zombie receives both rows.
            var zombieGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemRaiseZombie")!);
            var gemHost = inventory.Items(PlayerInventories.Weapon1).Single();
            inventory.Change(PlayerInventories.Weapon1, gemHost.Id, item => item with
            {
                Sockets = [.. item.Sockets, new ItemSocket(SocketColour.White, zombieGem)],
            });
            ushort zombieSource = Game.GemSkillEffects(conn)
                .Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == "RaiseZombie").Id;
            var corpseVariety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie")!;
            const uint corpseId = 0x1000c0de;
            var corpse = new SpawnedMonster("zombie corpse", new(100, 100, 0), 70, 8, 0)
            {
                Hostile = true, Dead = true, Life = 0, Variety = corpseVariety, Level = 70,
            };
            conn.Monsters[corpseId] = corpse;
            SetFinalDexterity(100);
            uint zombieId = await Game.SummonFighter("test", wire.Stream, wire.Ciphers, conn, zombieSource,
                Game.SummonData.Find("RaiseZombie")!, (100, 100), corpseId);
            Assert.NotEqual(0u, zombieId);
            var zombie = conn.Summons.Fighters[zombieId];
            Assert.Equal(4, zombie.Monster.Mods.Effects.MovementPct);
            var zombieWithSpeaker = zombie.Abilities.Where(a => !a.Attack.Spell).ToDictionary(a => a.Effect, a => a.Attack.Seconds);
            SetFinalDexterity(49);
            await Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, 4);
            Assert.Equal(0, zombie.Monster.Mods.Effects.MovementPct);
            foreach (var ability in zombie.Abilities.Where(a => !a.Attack.Spell))
            {
                Assert.True(ability.Attack.Seconds > zombieWithSpeaker[ability.Effect]);
                Assert.Equal(zombieWithSpeaker[ability.Effect] * 1.04, ability.Attack.Seconds, 6);
            }
            zombie.Monster.Dead = true; zombie.Monster.Life = 0;
            await Game.SummonsTick("test", wire.Stream, wire.Ciphers, conn, 4.6);
            Assert.DoesNotContain(zombieId, conn.Summons.Fighters.Keys);

            // Removing the worn source removes the coefficient without changing the owner's final Dexterity.
            var wornSpeaker = inventory.Items(PlayerInventories.Helmet).Single();
            inventory.Remove(PlayerInventories.Helmet, wornSpeaker.Id);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            SetFinalDexterity(100);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 5.2);
            Assert.Equal(baselineMovement, golem.Monster.Mods.Effects.MovementPct);
            foreach (var ability in golem.Abilities.Where(a => !a.Attack.Spell))
                Assert.Equal(baselineAttacks[ability.Effect], ability.Attack.Seconds, 6);
            conn.PlayerStats.Remove(finalDexterity);
            conn.PlayerStats.Remove(baseDexterity);

            uint increased = Program.Stats.Key("damage_+%")!.Value;
            Assert.Equal(20, conn.Golems.OwnerStats[increased]); Assert.Single(conn.Golems.Buffs);
            Game.SaveGolems(conn);
            var saved = JsonSerializer.Deserialize<GolemState.Saved[]>(CharacterSaves.ModuleData(conn.Character, "Game.Golems")!);
            Assert.Single(saved!); Assert.Equal(definition.Id, saved![0].Effect);

            var attacker = new SpawnedMonster("enemy", new(100, 100, 1), 100, 8, 1000);
            var spell = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Physical, 1000000, 1000000)) { Spell = true };
            // Fixed extreme values isolate the support multipliers from random crits and integer rounding.
            uint army = Program.Stats.Key("support_minion_totem_resistance_elemental_damage_+%_final")!.Value;
            var variety = Assert.IsType<MonsterCatalog.Variety>(golem.Monster.Variety);
            double FireDamage() => Game.GolemAbilities(conn, variety, golem.Monster.Level, golem, golem.Player)
                .Sum(a => a.Attack.Damage[DamageType.Fire].Max);
            double fire = FireDamage();
            Assert.True(fire > 0);
            golem.Own[army] = 100;
            Assert.Equal(2 * fire, FireDamage(), 5);
            golem.Own.Remove(army);

            uint shield = Program.Stats.Key("support_minion_defensive_stance_minion_damage_taken_+%_final")!.Value;
            golem.Own[shield] = -100;
            uint life = golem.Monster.Life;
            Game.GolemTakeHitLocked(conn, golem, 0x08000000, attacker, spell, 2);
            Assert.Equal(life, golem.Monster.Life);
            golem.Own.Remove(shield);
            Assert.True(Game.GolemTakeHitLocked(conn, golem, 0x08000000, attacker, spell, 3));
            Assert.True(golem.Monster.Dead); Assert.Equal(0u, golem.Monster.Life); Assert.Equal(5, golem.ReviveAt);
            await Game.RefreshGolemBuffs("test", wire.Stream, wire.Ciphers, conn);
            Assert.False(conn.Golems.OwnerStats.ContainsKey(increased)); Assert.Empty(conn.Golems.Buffs);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 4.9);
            Assert.True(golem.Monster.Dead);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 5);
            Assert.Single(conn.Golems.All); Assert.False(conn.Golems.All.Values.Single().Monster.Dead);
            Assert.Equal(20, conn.Golems.OwnerStats[increased]);

            // The revive replaced the golem object above; every Ancient Skull assertion must use the current live actor.
            golem = conn.Golems.All.Values.Single();
            void AssertCurrentGolem()
            {
                Assert.Single(conn.Golems.All);
                Assert.Same(golem, conn.Golems.All.Values.Single());
                Assert.False(golem.Monster.Dead);
                Assert.True(golem.Monster.Life > 0);
            }
            AssertCurrentGolem();

            // Ancient Skull's real coefficient uses the owner's maximum Power Charges, including Character.ot's base three.
            // Change only max_power_charges (the additive amount) so the refresh hash has to see the derived maximum.
            const int AncientSkullCriticalChancePerMaximumPowerCharge = 50;
            const string AncientSkullCriticalChanceMod = "MinionCriticalStrikeChanceMaximumPowerChargeUnique__1";
            var ancientSkull = new UniqueCatalog(Program.Items).Create("Ancient Skull", new Random(504))!;
            var ancientSkullCoefficient = ancientSkull.Mods.Single(mod => ItemModId(mod) == AncientSkullCriticalChanceMod);
            Assert.Equal(AncientSkullCriticalChancePerMaximumPowerCharge, ancientSkullCoefficient.Values[0]);
            var rawAncientSkullStats = ItemModStats.Sum([ancientSkull], Program.Items.Mods);
            Assert.True(rawAncientSkullStats.TryGetValue("minion_critical_strike_chance_+%_per_maximum_power_charge",
                out var rawAncientSkullChance), string.Join("|", rawAncientSkullStats.Select(p => $"{p.Key}={p.Value}")));
            Assert.Equal(AncientSkullCriticalChancePerMaximumPowerCharge, rawAncientSkullChance);
            int criticalWithoutAncientSkull = golem.Monster.Mods.Effects["critical_strike_chance_+%"];
            var actualCriticalAbility = golem.Abilities.First(a => a.Crit > 0);
            int baselineFinalCriticalChance = Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods);
            inventory.Add(PlayerInventories.Helmet, 0, 0, ancientSkull);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);

            uint maximumPowerCharges = Program.Stats.Key("max_power_charges")!.Value;
            var completedStats = Game.CharacterPlayerStats(conn);
            Assert.Equal(3, Charges.LimitsOf(ChargeType.Power, name =>
                completedStats.GetValueOrDefault(Program.Stats.Key(name) ?? 0)).Maximum);
            async Task<int> RefreshAncientSkullAt(double at, int additiveMaximum, int expectedMaximum, int expectedBonus)
            {
                conn.PlayerStats[maximumPowerCharges] = additiveMaximum;
                AssertCurrentGolem();
                var ownerStats = Game.CharacterPlayerStats(conn);
                int OwnerStat(string name) => Program.Stats.Key(name) is uint key
                    ? ownerStats.GetValueOrDefault(key) : 0;
                Assert.Equal(expectedMaximum, Charges.LimitsOf(ChargeType.Power, OwnerStat).Maximum);
                var session = Game.ChargesOf(conn);
                Assert.Equal(expectedMaximum, session.Limits(ChargeType.Power).Maximum);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, at);
                AssertCurrentGolem();
                Assert.Equal(expectedMaximum, session.Limits(ChargeType.Power).Maximum);
                Assert.Equal(criticalWithoutAncientSkull + expectedBonus,
                    golem.Monster.Mods.Effects["critical_strike_chance_+%"]);
                int finalChance = (int)Math.Clamp(actualCriticalAbility.Crit
                    * (100L + criticalWithoutAncientSkull + expectedBonus) / 100.0, 0, 10_000);
                Assert.Equal(finalChance, Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods));
                return finalChance;
            }

            _ = await RefreshAncientSkullAt(5.5, -3, 0, 0);
            _ = await RefreshAncientSkullAt(6, 0, 3, 3 * AncientSkullCriticalChancePerMaximumPowerCharge);
            int maximumFiveFinalCriticalChance = await RefreshAncientSkullAt(6.5, 2, 5,
                5 * AncientSkullCriticalChancePerMaximumPowerCharge);

            async Task SetPowerChargesAt(double at, int count, int expectedHeld)
            {
                AssertCurrentGolem();
                var ownerStats = Game.CharacterPlayerStats(conn);
                int OwnerStat(string name) => Program.Stats.Key(name) is uint key
                    ? ownerStats.GetValueOrDefault(key) : 0;
                Assert.Equal(5, Charges.LimitsOf(ChargeType.Power, OwnerStat).Maximum);
                var session = Game.ChargesOf(conn);
                lock (conn.Monsters)
                {
                    Assert.Equal(5, session.Limits(ChargeType.Power).Maximum);
                    session.Admit(at, [new ChargeEvent.Set(ChargeType.Power, count)], OwnerStat,
                        () => default, alive: true, new Random(1));
                    Assert.Equal(expectedHeld, session.Effective(ChargeType.Power));
                    Assert.Equal(expectedHeld, session.State.Held(ChargeType.Power));
                    Assert.Equal(5, session.Limits(ChargeType.Power).Maximum);
                }
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, at);
                AssertCurrentGolem();
                Assert.Equal(5, session.Limits(ChargeType.Power).Maximum);
                Assert.Equal(expectedHeld, session.Effective(ChargeType.Power));
                Assert.Equal(criticalWithoutAncientSkull + 5 * AncientSkullCriticalChancePerMaximumPowerCharge,
                    golem.Monster.Mods.Effects["critical_strike_chance_+%"]);
                Assert.Equal(maximumFiveFinalCriticalChance,
                    Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods));
            }

            await SetPowerChargesAt(7, 0, 0);
            await SetPowerChargesAt(7.5, 3, 3);

            var equippedAncientSkull = inventory.Items(PlayerInventories.Helmet).Single();
            inventory.Remove(PlayerInventories.Helmet, equippedAncientSkull.Id);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 8);
            AssertCurrentGolem();
            Assert.Equal(criticalWithoutAncientSkull, golem.Monster.Mods.Effects["critical_strike_chance_+%"]);
            Assert.Equal(baselineFinalCriticalChance,
                Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods));

            inventory.Add(PlayerInventories.Helmet, 0, 0, ancientSkull);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 8.5);
            AssertCurrentGolem();
            Assert.Equal(criticalWithoutAncientSkull + 5 * AncientSkullCriticalChancePerMaximumPowerCharge,
                golem.Monster.Mods.Effects["critical_strike_chance_+%"]);
            Assert.Equal(maximumFiveFinalCriticalChance,
                Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods));

            int actualFinalCriticalChance = Game.GolemCriticalChance(actualCriticalAbility, golem.Monster.Mods);
            Assert.InRange(actualFinalCriticalChance, 1, 9_999);
            Assert.True(Game.GolemCriticalStrike(actualCriticalAbility, golem.Monster.Mods, actualFinalCriticalChance - 1));
            Assert.False(Game.GolemCriticalStrike(actualCriticalAbility, golem.Monster.Mods, actualFinalCriticalChance));

            // Start a separate feedback connection so changing the maximum before summon cannot race the 9.0 refresh.
            const string feedbackCharacter = "AncientFeedback";
            ClientState? feedback = null;
            try
            {
                feedback = new ClientState(new(1, "ancient-feedback", 1000, 1000, 100, 100, null, []), 2, 1)
                {
                    Character = feedbackCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000, PlayerSent = true,
                    Experience = CharacterStats.ExperienceFor(100),
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    feedback.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                feedback.Golems.Restored = true;

                var feedbackInventory = Game.ItemsOf(feedback);
                var feedbackSkull = new UniqueCatalog(Program.Items).Create("Ancient Skull", new Random(504))!;
                var feedbackSkullLife = feedbackSkull.Mods.Single(mod =>
                    Program.Items.Mods.ByHash(mod.Hash)?.Id == "MinionLifeUnique__5_");
                int actualSkullLifeRoll = feedbackSkullLife.Values.Single();
                Assert.InRange(actualSkullLifeRoll, -40, -20);
                feedbackInventory.Add(PlayerInventories.Helmet, 0, 0, feedbackSkull);
                var feedbackGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
                var extraEnergyShield = Program.Items.Mods.ById(
                    "WeaponTreeMinionLifeAsExtraEnergyShieldReducedElementalResistance1")!;
                var extraEnergyShieldRoll = extraEnergyShield.Roll(ModSection.Explicit, new Random(504));
                Assert.Equal(15, extraEnergyShieldRoll.Values[0]);
                Assert.Equal(-6, extraEnergyShieldRoll.Values[1]);
                var feedbackWeapon = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, feedbackGem)], Mods = [.. ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!).Mods, extraEnergyShieldRoll],
                };
                feedbackInventory.Add(PlayerInventories.Weapon1, 0, 0, feedbackWeapon);
                var feedbackHost = feedbackInventory.Items(PlayerInventories.Weapon1).Single();
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);

                uint feedbackMaximumPowerCharges = Program.Stats.Key("max_power_charges")!.Value;
                feedback.PlayerStats[feedbackMaximumPowerCharges] = 61;
                var feedbackOwnerStats = Game.CharacterPlayerStats(feedback);
                Assert.Equal(64, Charges.LimitsOf(ChargeType.Power, name =>
                    feedbackOwnerStats.GetValueOrDefault(Program.Stats.Key(name) ?? 0)).Maximum);

                var feedbackDefinition = Game.GolemData.Find("SummonFlameGolem")!;
                var feedbackSource = Assert.Single(Game.GemSkillEffects(feedback),
                    g => Game.SkillsFor(feedback).EffectId(g.Effect) == "SummonFlameGolem");
                var energyShieldPackets = new List<byte[]>();
                var beforeEnergyShieldOutgoing = Program.Outgoing;
                Program.Outgoing = (ciphers, bytes) =>
                {
                    if (ReferenceEquals(ciphers, wire.Ciphers)) lock (energyShieldPackets) energyShieldPackets.Add(bytes.ToArray());
                    beforeEnergyShieldOutgoing?.Invoke(ciphers, bytes);
                };
                uint feedbackGolemId;
                try
                {
                    feedbackGolemId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, feedback,
                        feedbackSource.Id, feedbackDefinition, (100, 100));
                }
                finally { Program.Outgoing = beforeEnergyShieldOutgoing; }
                Assert.NotEqual(0u, feedbackGolemId);
                var feedbackGolem = feedback.Golems.All[feedbackGolemId];
                Assert.Same(feedbackGolem.Monster, feedback.Monsters[feedbackGolemId]);
                Assert.False(feedbackGolem.Monster.Dead);
                Assert.True(feedbackGolem.Monster.Life > 0);
                var feedbackAbility = feedbackGolem.Abilities.First(a => a.Crit > 0);
                Assert.True(feedbackAbility.Attack.Spell);
                Assert.Equal(64 * AncientSkullCriticalChancePerMaximumPowerCharge,
                    feedbackGolem.Monster.Mods.Effects["critical_strike_chance_+%"]);
                Assert.Equal(10_000, Game.GolemCriticalChance(feedbackAbility, feedbackGolem.Monster.Mods));
                int feedbackOwnLifeMore = feedbackGolem.Own.GetValueOrDefault(
                    Program.Stats.Key("active_skill_minion_life_+%_final")!.Value);
                Assert.Equal(0, feedbackOwnLifeMore);
                Assert.Equal(0, feedbackGolem.Own.GetValueOrDefault(
                    Program.Stats.Key("support_minion_maximum_life_+%_final")!.Value));
                Assert.Equal(0, feedbackGolem.Own.GetValueOrDefault(
                    Program.Stats.Key("support_minion_damage_minion_life_+%_final")!.Value));
                Assert.Equal(0, feedbackGolem.Own.GetValueOrDefault(
                    Program.Stats.Key("minion_maximum_life_+%")!.Value));
                Assert.Equal(actualSkullLifeRoll, feedbackGolem.Player.GetValueOrDefault(
                    Program.Stats.Key("minion_maximum_life_+%")!.Value));
                Assert.Equal(0, feedbackGolem.Player.GetValueOrDefault(
                    Program.Stats.Key("golem_maximum_life_+%")!.Value));
                Assert.Equal(0, feedbackGolem.Player.GetValueOrDefault(
                    Program.Stats.Key("active_skill_non_reaper_minion_maximum_life_+%_final")!.Value));
                Assert.Equal(0, feedbackGolem.Player.GetValueOrDefault(
                    Program.Stats.Key("elemental_golems_maximum_life_is_doubled")!.Value));
                Assert.Equal(0, feedbackGolem.Gear.GetValueOrDefault("base_maximum_life"));
                Assert.Equal(0, feedbackGolem.Gear.GetValueOrDefault("maximum_life_+%"));
                Assert.Equal(15, feedbackGolem.Monster.Mods.Effects[
                    "maximum_life_%_to_add_as_maximum_energy_shield"]);
                Assert.Equal(0, feedbackGolem.Monster.Mods.Effects["maximum_energy_shield_+%"]);
                Assert.Equal(0, feedbackGolem.Monster.Mods.Effects["global_defences_+%"]);
                Assert.Equal(0, feedbackGolem.Monster.Mods.Effects["energy_shield_+%_final_for_minion"]);
                Assert.Equal(0, feedbackGolem.Own.GetValueOrDefault(
                    Program.Stats.Key("active_skill_minion_energy_shield_+%_final")!.Value));
                Assert.Equal(0, feedbackGolem.Auras.GetValueOrDefault("base_maximum_energy_shield"));
                Assert.Equal(0, feedbackGolem.Gear.GetValueOrDefault("base_maximum_energy_shield"));
                var actualVariety = feedbackGolem.Monster.Variety!;
                byte actualLevel = feedbackGolem.Monster.Level;
                double expectedServerShield = .15 * (double)Game.Monsters.BaseLife(actualVariety, actualLevel)!.Value
                    * (1 + actualSkullLifeRoll / 100d);
                uint expectedEnergyShield = (uint)Math.Floor(expectedServerShield);
                var feedbackPools = Game.GolemPools.Calculate(Game.Monsters, actualVariety, actualLevel,
                    feedbackGolem, feedbackGolem.Player, feedbackGolem.Monster.Mods.Effects);
                Assert.Equal(expectedServerShield, feedbackPools.MaximumEnergyShield, 10);
                Assert.Equal(expectedEnergyShield, feedbackGolem.MaximumShield);
                Assert.Equal(feedbackGolem.MaximumShield, feedbackGolem.Shield);
                var energyShieldOrigin = feedback.MinionOrigins[feedbackGolemId];
                Assert.Equal(expectedEnergyShield, energyShieldOrigin.Object.EnergyShield);
                Assert.Equal(expectedEnergyShield, (uint)energyShieldOrigin.Object.Stats!.Single(
                    stat => stat.Key == maximumEnergyShield).Value);
                var emittedCreate = Assert.Single(energyShieldPackets, bytes => bytes.Length >= 18
                    && BinaryPrimitives.ReadUInt16BigEndian(bytes) == InstanceEntities.CreateObjectId
                    && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) == feedbackGolemId);
                int blobLength = BinaryPrimitives.ReadUInt16BigEndian(emittedCreate.AsSpan(16));
                var emittedBlob = emittedCreate.AsSpan(18, blobLength).ToArray();
                int cursor = 2 + 18;
                uint Compact()
                {
                    byte first = emittedBlob[cursor++];
                    int following = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
                    uint value = following == 4 ? 0u : (uint)(first & (0x7f >> following));
                    for (int i = 0; i < following; i++) value = (value << 8) | emittedBlob[cursor++];
                    return value;
                }
                var emittedStats = new Dictionary<uint, uint>();
                for (uint count = Compact(), i = 0; i < count; i++) emittedStats[Compact()] = Compact();
                Assert.Equal(expectedEnergyShield, emittedStats[maximumEnergyShield]);
                byte[] Pool(uint current)
                {
                    var pool = new byte[18];
                    BinaryPrimitives.WriteUInt32LittleEndian(pool, current);
                    return pool;
                }
                Assert.True(emittedBlob.IndexOf([.. Pool(feedbackGolem.Monster.Life), .. Pool(0), .. Pool(expectedEnergyShield)]) >= 0);

                const uint feedbackTargetId = 0x08000002;
                const double feedbackHitAt = 1;
                var feedbackFrom = feedbackGolem.Monster.Walk.Position(feedbackHitAt);
                var feedbackTarget = new SpawnedMonster("ancient skull feedback target",
                    new(feedbackFrom.X + 17, feedbackFrom.Y - 9, 0), 100, 8, 1_000_000);
                feedback.Monsters[feedbackTargetId] = feedbackTarget;
                int feedbackOutputStart = feedback.Minions.OutputQueue.Count;
                uint lifeBeforeFeedbackHit = feedbackTarget.Life;
                try
                {
                    Assert.True(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, feedback, feedbackGolem,
                        feedbackAbility, feedbackTargetId, feedbackTarget, feedbackHitAt));
                }
                finally
                {
                    feedback.Monsters.Remove(feedbackTargetId);
                }
                Assert.True(feedbackTarget.Life < lifeBeforeFeedbackHit);

                var feedbackOutputs = feedback.Minions.OutputQueue.Skip(feedbackOutputStart).ToArray();
                var hitOutput = feedbackOutputs.Single(output => output.Packet.Length >= 2
                    && BinaryPrimitives.ReadUInt16BigEndian(output.Packet) == InstanceProtocol.ActionHitId);
                var hitPacket = hitOutput.Packet;
                Assert.Equal(25, hitPacket.Length);
                Assert.Equal(feedbackGolem.Id, BinaryPrimitives.ReadUInt32BigEndian(hitPacket.AsSpan(2)));
                Assert.Equal((ushort)0xffff, BinaryPrimitives.ReadUInt16BigEndian(hitPacket.AsSpan(12)));
                Assert.Equal(feedbackTargetId, BinaryPrimitives.ReadUInt32BigEndian(hitPacket.AsSpan(16)));
                Assert.Equal((ushort)0x4221, BinaryPrimitives.ReadUInt16BigEndian(hitPacket.AsSpan(20)));
                Assert.Equal(0x03, hitPacket[22]);
                Assert.Equal(Game.HitCode(feedbackAbility.Attack.Damage), hitPacket[23]);
                var feedbackTo = feedbackTarget.Walk.Position(feedbackHitAt);
                Assert.Equal(Game.HitAngle((int)feedbackTo.X - feedbackFrom.X, (int)feedbackTo.Y - feedbackFrom.Y), hitPacket[24]);
                var lifeOutput = feedbackOutputs.Single(output => output.Kind == PlayerMinions.OutputKind.Life);
                Assert.Equal(feedbackTargetId, lifeOutput.TargetId);
                Assert.Same(feedbackTarget, lifeOutput.Target);
                Assert.Equal(feedbackTarget.Life, lifeOutput.Target!.Life);

                async Task<List<byte[]>> Capture(ConnectionCiphers ciphers, Func<Task> send)
                {
                    var packets = new List<byte[]>();
                    var previous = Program.Outgoing;
                    Program.Outgoing = (seen, bytes) =>
                    {
                        if (ReferenceEquals(seen, ciphers)) lock (packets) packets.Add(bytes.ToArray());
                        previous?.Invoke(seen, bytes);
                    };
                    try { await send(); }
                    finally { Program.Outgoing = previous; }
                    return packets;
                }
                void AssertEnergyShieldEmission(IReadOnlyList<byte[]> packets, uint id, uint maximum, uint current)
                {
                    var maximumPacket = InstanceEntities.StatsUpdate(id, [(maximumEnergyShield, (int)maximum)]);
                    var currentPacket = InstanceProtocol.LifeUpdate(id, current, pool: 2);
                    Assert.Contains(packets, packet =>
                    {
                        var bytes = packet.AsSpan();
                        int atMaximum = bytes.IndexOf(maximumPacket), atCurrent = bytes.IndexOf(currentPacket);
                        return atMaximum >= 0 && atCurrent > atMaximum;
                    });
                }
                uint MaximumFrom() => (uint)feedback.MinionOrigins[feedbackGolemId].Object.Stats!
                    .Single(stat => stat.Key == maximumEnergyShield).Value;

                Assert.Equal((uint)int.MaxValue, Game.NativeEnergyShield(double.PositiveInfinity));
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 2.05);
                var stableStats = feedback.MinionOrigins[feedbackGolemId].Object.Stats;
                var stableAbilities = feedbackGolem.Abilities;
                feedbackGolem.LastStep = 2;
                var esAttacker = new SpawnedMonster("energy shield attacker", new(100, 100, 1), 100, 8, 1_000_000);
                var esHit = new MonsterAttack(0x4000, 1, 100,
                    DamagePacket.Of(DamageType.Fire, Math.Max(1, (int)expectedEnergyShield / 4),
                        Math.Max(1, (int)expectedEnergyShield / 4))) { Spell = true };
                uint esLifeBefore = feedbackGolem.Monster.Life;
                double esBefore = feedbackGolem.Shield;
                Assert.True(Game.GolemTakeHitLocked(feedback, feedbackGolem, 0x08000000, esAttacker, esHit, 2.1));
                Assert.Equal(esLifeBefore, feedbackGolem.Monster.Life);
                Assert.True(feedbackGolem.Shield < esBefore);
                Assert.Same(stableStats, feedback.MinionOrigins[feedbackGolemId].Object.Stats);
                Assert.Same(stableAbilities, feedbackGolem.Abilities);
                uint currentAfterAbsorption = Game.NativeEnergyShield(feedbackGolem.Shield);
                var absorptionPackets = await Capture(wire.Ciphers,
                    () => Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, feedback));
                AssertEnergyShieldEmission(absorptionPackets, feedbackGolemId, MaximumFrom(), currentAfterAbsorption);

                var chaosHit = esHit with { Damage = DamagePacket.Of(DamageType.Chaos, 1, 1) };
                double shieldBeforeChaos = feedbackGolem.Shield; uint lifeBeforeChaos = feedbackGolem.Monster.Life;
                Assert.True(Game.GolemTakeHitLocked(feedback, feedbackGolem, 0x08000000, esAttacker, chaosHit, 2.15));
                Assert.Equal(shieldBeforeChaos, feedbackGolem.Shield);
                Assert.Equal(lifeBeforeChaos - 1, feedbackGolem.Monster.Life);

                // Both hits happen before the real drain: the one deduplicated output must project current zero,
                // never the positive value captured by the first hit. The physical remainder spills to life.
                int spill = Math.Max(1, (int)Math.Floor((double)feedbackGolem.Shield) / 2);
                var spillHit = esHit with { Damage = DamagePacket.Of(DamageType.Physical, spill, spill) };
                uint lifeBeforeSpill = feedbackGolem.Monster.Life;
                Assert.True(Game.GolemTakeHitLocked(feedback, feedbackGolem, 0x08000000, esAttacker, spillHit, 2.2));
                Assert.True(feedbackGolem.Shield > 0);
                int remainder = (int)Math.Floor((double)feedbackGolem.Shield) + 7;
                Assert.True(Game.GolemTakeHitLocked(feedback, feedbackGolem, 0x08000000, esAttacker,
                    spillHit with { Damage = DamagePacket.Of(DamageType.Physical, remainder, remainder) }, 2.25));
                Assert.Equal(0u, feedbackGolem.Shield);
                Assert.Equal(lifeBeforeSpill - 7, feedbackGolem.Monster.Life);
                var deferredPackets = await Capture(wire.Ciphers,
                    () => Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, feedback));
                AssertEnergyShieldEmission(deferredPackets, feedbackGolemId, MaximumFrom(), 0);
                Assert.Contains(deferredPackets, packet => packet.AsSpan().IndexOf(
                    InstanceProtocol.LifeUpdate(feedbackGolemId, feedbackGolem.Monster.Life)) >= 0);

                feedbackGolem.LastStep = 2.5;
                var rechargePackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 5));
                uint recharged = Game.NativeEnergyShield(feedbackGolem.Shield);
                Assert.InRange(recharged, 1u, MaximumFrom() - 1);
                AssertEnergyShieldEmission(rechargePackets, feedbackGolemId, MaximumFrom(), recharged);
                var rechargedStats = feedback.MinionOrigins[feedbackGolemId].Object.Stats;
                var rechargedAbilities = feedbackGolem.Abilities;
                var repeatedPackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 5));
                Assert.DoesNotContain(repeatedPackets, packet => packet.AsSpan().IndexOf(
                    InstanceProtocol.LifeUpdate(feedbackGolemId, recharged, pool: 2)) >= 0);
                Assert.Same(rechargedStats, feedback.MinionOrigins[feedbackGolemId].Object.Stats);
                Assert.Same(rechargedAbilities, feedbackGolem.Abilities);

                // Removing only the real tree modifier keeps the gem host, item id and sockets intact. The maximum
                // stat is explicitly zeroed and pool 2 is zero in the same live-source output.
                var baseBow = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!);
                feedbackInventory.Change(PlayerInventories.Weapon1, feedbackHost.Id,
                    item => item with { Mods = baseBow.Mods });
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                var sourceOffPackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 5.6));
                Assert.Equal(0u, feedbackGolem.MaximumShield);
                Assert.Equal(0u, feedbackGolem.Shield);
                AssertEnergyShieldEmission(sourceOffPackets, feedbackGolemId, 0, 0);
                Assert.Equal(0, Assert.Single(feedback.MinionOrigins[feedbackGolemId].Object.Stats!,
                    stat => stat.Key == maximumEnergyShield).Value);

                // A peer created only after the real provider retires receives an explicit zero maximum and zero pool.
                using var zeroPeerWire = new Wire();
                var zeroPeer = new ClientState(new(1, "energy-shield-zero-peer", 1000, 1000, 100, 100, null, []), 1, 1)
                {
                    Character = "EnergyShieldZeroPeer", Instance = feedback.Instance, PlayerSent = true,
                    PlayerLife = 10000, PlayerMana = 10000,
                };
                feedback.Instance.PublishPlayer(new(feedback.PeerId, 2, new("Owner", 1, 100, 100, 100, 100),
                    100, 100, 100, feedback));
                feedback.Instance.PublishPlayer(new(zeroPeer.PeerId, 2, new("ZeroPeer", 1, 100, 100, 100, 100),
                    100, 100, 100, zeroPeer));
                Game.PublishAwakeMonsters(feedback);
                var zeroPeerPackets = await Capture(zeroPeerWire.Ciphers,
                    () => Game.SyncPeerMinions("zero-peer", zeroPeerWire.Stream, zeroPeerWire.Ciphers, zeroPeer));
                uint zeroMirroredId = zeroPeer.MirroredObjects[(feedback.PeerId, feedbackGolemId)];
                var zeroPublished = Assert.Single(feedback.PublishedMinions, minion => minion.Id == feedbackGolemId);
                var zeroOrigin = feedback.MinionOrigins[feedbackGolemId];
                Assert.Equal(0u, zeroOrigin.Object.EnergyShield);
                Assert.Equal(0, Assert.Single(zeroOrigin.Object.Stats!, stat => stat.Key == maximumEnergyShield).Value);
                var zeroCreate = InstanceEntities.CreateObject(zeroMirroredId, zeroOrigin.Type, (zeroOrigin.Object with
                {
                    GridX = zeroPublished.X, GridY = zeroPublished.Y, Life = zeroPublished.Life, EnergyShield = 0,
                }).Blob());
                Assert.Contains(zeroPeerPackets, packet => packet.AsSpan().IndexOf(zeroCreate) >= 0);
                feedback.Instance.RemovePlayer(zeroPeer.PeerId);

                feedbackInventory.Change(PlayerInventories.Weapon1, feedbackHost.Id,
                    item => item with { Mods = [.. baseBow.Mods, extraEnergyShieldRoll] });
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                var sourceOnPackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 6.2));
                uint partialAfterReturn = Game.NativeEnergyShield(feedbackGolem.Shield);
                Assert.InRange(partialAfterReturn, 1u, MaximumFrom() - 1);
                AssertEnergyShieldEmission(sourceOnPackets, feedbackGolemId, MaximumFrom(), partialAfterReturn);

                // A late peer receives the current absorbed/recharged value, not a full-shield birth snapshot.
                using var peerWire = new Wire();
                var peer = new ClientState(new(1, "energy-shield-peer", 1000, 1000, 100, 100, null, []), 1, 1)
                {
                    Character = "EnergyShieldPeer", Instance = feedback.Instance, PlayerSent = true,
                    PlayerLife = 10000, PlayerMana = 10000,
                };
                feedback.Instance.PublishPlayer(new(feedback.PeerId, 2, new("Owner", 1, 100, 100, 100, 100),
                    100, 100, 100, feedback));
                feedback.Instance.PublishPlayer(new(peer.PeerId, 2, new("Peer", 1, 100, 100, 100, 100),
                    100, 100, 100, peer));
                Game.PublishAwakeMonsters(feedback);
                var lateCreatePackets = await Capture(peerWire.Ciphers,
                    () => Game.SyncPeerMinions("peer", peerWire.Stream, peerWire.Ciphers, peer));
                uint mirroredId = peer.MirroredObjects[(feedback.PeerId, feedbackGolemId)];
                var published = Assert.Single(feedback.PublishedMinions, minion => minion.Id == feedbackGolemId);
                var mirroredOrigin = feedback.MinionOrigins[feedbackGolemId];
                var lateCreate = InstanceEntities.CreateObject(mirroredId, mirroredOrigin.Type, (mirroredOrigin.Object with
                {
                    GridX = published.X, GridY = published.Y, Life = published.Life, EnergyShield = partialAfterReturn,
                }).Blob());
                Assert.Contains(lateCreatePackets, packet => packet.AsSpan().IndexOf(lateCreate) >= 0);
                Assert.NotEqual(MaximumFrom(), partialAfterReturn);

                // The peer's retirement change contains maximum first, current second, including both explicit zeroes.
                feedbackInventory.Change(PlayerInventories.Weapon1, feedbackHost.Id,
                    item => item with { Mods = baseBow.Mods });
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 7);
                Game.PublishAwakeMonsters(feedback);
                var peerRetirementPackets = await Capture(peerWire.Ciphers,
                    () => Game.SyncPeerMinions("peer", peerWire.Stream, peerWire.Ciphers, peer));
                AssertEnergyShieldEmission(peerRetirementPackets, mirroredId, 0, 0);

                feedbackInventory.Change(PlayerInventories.Weapon1, feedbackHost.Id,
                    item => item with { Mods = [.. baseBow.Mods, extraEnergyShieldRoll] });
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 7.6);
                Assert.InRange(Game.NativeEnergyShield(feedbackGolem.Shield), 1u, MaximumFrom() - 1);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 10.7);
                Assert.Equal(MaximumFrom(), Game.NativeEnergyShield(feedbackGolem.Shield));

                // Queue a real positive ES update while retirement is still ineligible, then remove the real host.
                int retirementOutputStart = feedback.Minions.OutputQueue.Count;
                var retirementHit = esHit with { Damage = DamagePacket.Of(DamageType.Fire,
                    Math.Max(1, (int)MaximumFrom() / 4), Math.Max(1, (int)MaximumFrom() / 4)) };
                uint retirementLife = feedbackGolem.Monster.Life;
                Assert.True(Game.GolemTakeHitLocked(feedback, feedbackGolem, 0x08000000, esAttacker, retirementHit, 10.8));
                Assert.Equal(retirementLife, feedbackGolem.Monster.Life);
                uint queuedPositive = Game.NativeEnergyShield(feedbackGolem.Shield);
                Assert.InRange(queuedPositive, 1u, MaximumFrom() - 1);
                Assert.Single(feedback.Minions.OutputQueue.Skip(retirementOutputStart),
                    output => output.Kind == PlayerMinions.OutputKind.EnergyShield
                        && output.TargetId == feedbackGolemId);

                feedbackInventory.Remove(PlayerInventories.Weapon1, feedbackHost.Id);
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                var retiredPackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 11.3));
                Assert.False(feedback.MinionOrigins.ContainsKey(feedbackGolemId));
                Assert.DoesNotContain(retiredPackets, packet => packet.AsSpan().IndexOf(
                    InstanceProtocol.LifeUpdate(feedbackGolemId, 0, pool: 2)) >= 0);
                Assert.DoesNotContain(retiredPackets, packet => packet.AsSpan().IndexOf(
                    InstanceProtocol.LifeUpdate(feedbackGolemId, queuedPositive, pool: 2)) >= 0);

                // A positive native ES actor crossing the fatal conversion in the in-step refresh emits an explicit
                // zero maximum/current and performs no recovery or action after the refresh marked it dead.
                feedbackInventory.Add(PlayerInventories.Weapon1, 0, 0, feedbackWeapon);
                Game.EquipmentStatSources(feedback, feedbackInventory.EquipmentSnapshot());
                feedback.Pools = Game.PoolsFor(feedback, feedback.Pools.Level);
                var fatalSource = Assert.Single(Game.GemSkillEffects(feedback),
                    g => Game.SkillsFor(feedback).EffectId(g.Effect) == "SummonFlameGolem");
                uint fatalId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, feedback,
                    fatalSource.Id, feedbackDefinition, (100, 100));
                var fatalGolem = feedback.Golems.All[fatalId];
                uint fatalBornMaximum = Game.NativeEnergyShield(fatalGolem.MaximumShield);
                var fatalBornOrigin = feedback.MinionOrigins[fatalId];
                Assert.True(fatalBornMaximum > 0);
                Assert.Equal(fatalBornMaximum, Game.NativeEnergyShield(fatalGolem.Shield));
                Assert.True(fatalBornOrigin.Object.EnergyShield > 0);
                Assert.Equal((int)fatalBornMaximum,
                    fatalBornOrigin.Object.Stats!.Single(stat => stat.Key == maximumEnergyShield).Value);
                feedback.Golems.RefreshAt = double.PositiveInfinity;
                fatalGolem.Player[Program.Stats.Key(
                    "minion_maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance")!.Value] = 2;
                fatalGolem.Player[Program.Stats.Key("minion_chaos_resistance_%")!.Value] = 40;
                double fatalLastStep = fatalGolem.LastStep;
                var fatalPackets = await Capture(wire.Ciphers,
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, feedback, 11.2));
                Assert.True(fatalGolem.Monster.Dead);
                Assert.Equal(0u, fatalGolem.Monster.Life);
                Assert.Equal(1u, fatalGolem.Monster.MaxLife);
                Assert.Equal(0u, fatalGolem.MaximumShield);
                Assert.Equal(0u, fatalGolem.Shield);
                Assert.Null(fatalGolem.Pending);
                Assert.Equal(fatalLastStep, fatalGolem.LastStep);
                var fatalOrigin = feedback.MinionOrigins[fatalId];
                Assert.Equal(0u, fatalOrigin.Object.EnergyShield);
                Assert.Empty(fatalOrigin.Object.Skills ?? []);
                Assert.Equal(0, Assert.Single(fatalOrigin.Object.Stats!,
                    stat => stat.Key == maximumEnergyShield).Value);
                var fatalMaximumPacket = InstanceEntities.StatsUpdate(fatalId, [(maximumEnergyShield, 0)]);
                var fatalCurrentPacket = InstanceProtocol.LifeUpdate(fatalId, 0, pool: 2);
                Assert.Contains(fatalPackets, packet =>
                {
                    var bytes = packet.AsSpan();
                    int maximumAt = bytes.IndexOf(fatalMaximumPacket), currentAt = bytes.IndexOf(fatalCurrentPacket);
                    return maximumAt >= 0 && currentAt > maximumAt;
                });
            }
            finally
            {
                if (feedback is not null)
                {
                    feedback.Closed = true;
                    foreach (var player in feedback.Instance.Presence.PlayersExcept(feedback.PeerId))
                        feedback.Instance.RemovePlayer(player.Id);
                    PlayerInventories.Forget(feedbackCharacter);
                    Game.InactiveItems.TryRemove(feedbackCharacter, out _);
                }
            }

            const string fleshCharacter = "FleshPools";
            var fleshSchedule = new AreaClockSchedule();
            var fleshUpdates = new PlayerUpdatesState(lifetime, fleshSchedule);
            var previousFleshContext = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = fleshUpdates;
            ClientState? flesh = null;
            try
            {
                fleshSchedule.At = 6;
                flesh = new ClientState(new(1, "flesh-pools", 1000, 1000, 100, 100, null, []), 4, 1)
                {
                    Character = fleshCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                    PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = fleshUpdates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                flesh.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    flesh.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                flesh.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                // Diagnostic-only fixture control: cancel the golem controller's ordinary 1% life/second regeneration.
                flesh.PlayerStats[Program.Stats.Key("minion_life_regeneration_rate_per_minute_%")!.Value] = -60;

                var fleshInventory = Game.ItemsOf(flesh);
                var flameGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
                var chaosGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemSummonChaosGolem")!);
                fleshInventory.Add(PlayerInventories.Weapon1, 0, 0,
                    ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                    { Sockets = [new(SocketColour.White, flameGem), new(SocketColour.White, chaosGem)], Links = [2] });
                void RecalculateFlesh()
                {
                    Game.EquipmentStatSources(flesh, fleshInventory.EquipmentSnapshot());
                    flesh.Pools = Game.PoolsFor(flesh, flesh.Pools.Level);
                    flesh.PlayerLife = flesh.Pools.Life;
                    flesh.PlayerMana = flesh.Pools.Mana;
                }
                RecalculateFlesh();
                var flameSource = Assert.Single(Game.GemSkillEffects(flesh),
                    grant => Game.SkillsFor(flesh).EffectId(grant.Effect) == "SummonFlameGolem");
                var chaosSource = Assert.Single(Game.GemSkillEffects(flesh),
                    grant => Game.SkillsFor(flesh).EffectId(grant.Effect) == "SummonChaosGolem");
                var flameDefinition = Game.GolemData.Find("SummonFlameGolem")!;
                var chaosDefinition = Game.GolemData.Find("SummonChaosGolem")!;
                var flameVariety = Game.Monsters.Find(flameDefinition.Variety)!;
                var chaosVariety = Game.Monsters.Find(chaosDefinition.Variety)!;
                Assert.Equal(20, HitMitigation.Resistance(Game.Monsters, flameVariety, 70, DamageType.Chaos).Value);
                Assert.Equal(60, HitMitigation.Resistance(Game.Monsters, chaosVariety, 70, DamageType.Chaos).Value);

                uint flameId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, flesh,
                    flameSource.Id, flameDefinition, (100, 100));
                uint chaosId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, flesh,
                    chaosSource.Id, chaosDefinition, (100, 100));
                Assert.NotEqual(0u, flameId);
                Assert.NotEqual(0u, chaosId);
                var flame = flesh.Golems.All[flameId];
                var chaos = flesh.Golems.All[chaosId];
                Assert.False(flame.Monster.Dead);
                Assert.False(chaos.Monster.Dead);

                var fleshArmour = new UniqueCatalog(Program.Items).Create("Fleshcrafter", new Random(504))!;
                var conversionMod = Program.Items.Mods.ById("MinionLifeConvertedToEnergyShieldUnique__1")!;
                Assert.Equal(2, conversionMod.Stats[0].Min);
                Assert.Equal("minion_maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance",
                    conversionMod.Stats[0].Id);
                string[] fleshRows =
                [
                    "LocalIncreasedEnergyShieldUniqueBodyInt1", "MinionLifeConvertedToEnergyShieldUnique__1",
                    "MinionChaosDamageDoesNotBypassESUnique__1", "MinionEnergyShieldRechargeDelayUnique__1",
                    "MinionHitsIgnoreResistanceWithESUnique__1_",
                ];
                foreach (string row in fleshRows)
                    Assert.Contains(fleshArmour.Mods, mod => Program.Items.Mods.ByHash(mod.Hash)?.Id == row);
                Assert.Equal(5, fleshArmour.Mods.Count(mod =>
                    fleshRows.Contains(Program.Items.Mods.ByHash(mod.Hash)?.Id)));
                Assert.Equal(2, fleshArmour.Mods.Single(mod => mod.Hash == conversionMod.Hash).Values[0]);
                var fleshArmourSlot = fleshInventory.Add(PlayerInventories.BodyArmour, 0, 0, fleshArmour);
                RecalculateFlesh();

                async Task<List<byte[]>> CaptureFlesh(Func<Task> send)
                {
                    var packets = new List<byte[]>();
                    var previous = Program.Outgoing;
                    Program.Outgoing = (seen, bytes) =>
                    {
                        if (ReferenceEquals(seen, wire.Ciphers)) lock (packets) packets.Add(bytes.ToArray());
                        previous?.Invoke(seen, bytes);
                    };
                    try { await send(); }
                    finally { Program.Outgoing = previous; }
                    return packets;
                }

                fleshSchedule.At = 6.5;
                var refreshPackets = await CaptureFlesh(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At));
                var flameConversion = Game.GolemPools.Calculate(Game.Monsters, flame.Monster.Variety!,
                    flame.Monster.Level, flame, flame.Player, flame.Monster.Mods.Effects);
                Assert.Equal(40, flameConversion.ConversionPercent);
                Assert.InRange(flameConversion.MaximumLife, 1u, (uint)int.MaxValue);
                Assert.Equal(flameConversion.MaximumLife, flame.Monster.MaxLife);
                Assert.True(chaos.Monster.Dead);
                Assert.Equal(0u, chaos.Monster.Life);
                Assert.Equal(1u, chaos.Monster.MaxLife);
                Assert.Empty(chaos.Abilities);
                Assert.Empty(chaos.Skills);
                Assert.Empty(chaos.OwnerBuff);
                Assert.Empty(chaos.Hazards);
                Assert.Contains(refreshPackets, packet => packet.AsSpan().IndexOf(
                    InstanceProtocol.LifeUpdate(chaosId, 0)) >= 0);

                // The second automatic Chaos Golem is admitted through the ordinary create shape, then becomes a real
                // dead roster actor in the same create/wake gate. Its transport maximum stays positive.
                fleshSchedule.At = 8.5;
                var revivePackets = await CaptureFlesh(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At));
                var revivedChaos = Assert.Single(flesh.Golems.All.Values,
                    golem => golem.Definition.Kind == "Chaos" && golem.Id != chaosId);
                Assert.NotEqual(chaosId, revivedChaos.Id);
                Assert.True(revivedChaos.Monster.Dead);
                Assert.Equal(0u, revivedChaos.Monster.Life);
                Assert.Equal(1u, revivedChaos.Monster.MaxLife);
                var chaosOrigin = flesh.MinionOrigins[revivedChaos.Id];
                var createPacket = Assert.Single(revivePackets, packet => packet.Length >= 18
                    && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId
                    && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == revivedChaos.Id);
                var deathPacket = InstanceProtocol.LifeUpdate(revivedChaos.Id, 0);
                Assert.Contains(revivePackets, packet =>
                {
                    var bytes = packet.AsSpan();
                    int createAt = bytes.IndexOf(createPacket), deathAt = bytes.IndexOf(deathPacket);
                    return createAt >= 0 && deathAt > createAt;
                });
                Assert.Equal(1u, chaosOrigin.Object.Life);
                Assert.Equal(1, chaosOrigin.Object.Stats!.Single(stat => stat.Key == StatKeys.MaximumLife).Value);

                Game.SaveGolems(flesh);
                var savedFlesh = JsonSerializer.Deserialize<GolemState.Saved[]>(
                    CharacterSaves.ModuleData(flesh.Character, "Game.Golems")!)!;
                var savedChaos = Assert.Single(savedFlesh, saved => saved.Effect == chaosDefinition.Id);
                Assert.Equal(0, savedChaos.Life);
                Assert.True(double.IsFinite(savedChaos.ReviveSeconds));
                Assert.InRange(savedChaos.ReviveSeconds, .001, 2);

                // The actual Bonemeld coefficient inherits the owner's final Chaos resistance into the prepared minion
                // effects before the same capped Chaos resistance and conversion calculation runs.
                var bonemeld = new UniqueCatalog(Program.Items).Create("Bonemeld", new Random(504))!;
                var inherit = bonemeld.Mods.Single(mod =>
                    Program.Items.Mods.ByHash(mod.Hash)?.Id == "MinionsGainPercentOfYourResistancesUnique_1");
                Assert.Equal(50, inherit.Values[0]);
                var amethystRing = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring10")!);
                Assert.Contains(amethystRing.Mods, mod => Program.Items.Mods.ByHash(mod.Hash)?.Stats.Any(
                    stat => stat.Id == "base_chaos_damage_resistance_%") == true);
                var bonemeldSlot = fleshInventory.Add(5, 0, 0, bonemeld);
                var amethystRingSlot = fleshInventory.Add(6, 0, 0, amethystRing);
                RecalculateFlesh();
                Assert.DoesNotContain(bonemeldSlot.Id, flesh.StatSourcesCache!.Inactive);
                int ownerChaos = Game.Resistance(flesh, DamageType.Chaos);
                Assert.InRange(ownerChaos, 1, GameServer.Core.World.DamageModel.MaxResistCap);
                fleshSchedule.At = 9;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At);
                var inherited = Game.GolemPools.Calculate(Game.Monsters, flame.Monster.Variety!, flame.Monster.Level,
                    flame, flame.Player, flame.Monster.Mods.Effects);
                int inheritedChaos = (int)Math.Floor(ownerChaos * inherit.Values[0] / 100d);
                int expectedMinionChaos = 20 + inheritedChaos;
                int conversionCoefficient = flame.Monster.Mods.Effects[
                    "maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"];
                int expectedConversion = Math.Clamp(conversionCoefficient * Math.Max(0, expectedMinionChaos), 0, 100);
                Assert.Equal(expectedMinionChaos, inherited.ChaosResistance);
                Assert.Equal(expectedConversion, inherited.ConversionPercent);
                Assert.True(inherited.ConversionPercent < 100);
                fleshInventory.Remove(5, bonemeldSlot.Id);
                fleshInventory.Remove(6, amethystRingSlot.Id);
                RecalculateFlesh();
                fleshSchedule.At = 9.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At);
                var restoredFlame = Game.GolemPools.Calculate(Game.Monsters, flame.Monster.Variety!, flame.Monster.Level,
                    flame, flame.Player, flame.Monster.Mods.Effects);
                Assert.Equal(flameConversion.ChaosResistance, restoredFlame.ChaosResistance);
                Assert.Equal(flameConversion.ConversionPercent, restoredFlame.ConversionPercent);

                // Source off/on uses absolute current pools on the same actor: an increased maximum neither heals nor
                // refills, and a decreased maximum only clamps down.
                uint damagedLife = flame.Monster.MaxLife - 7;
                flame.Monster.Life = damagedLife;
                fleshInventory.Remove(PlayerInventories.BodyArmour, fleshArmourSlot.Id);
                RecalculateFlesh();
                fleshSchedule.At = 10;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At);
                Assert.Same(flame, flesh.Golems.All[flameId]);
                Assert.True(flame.Monster.MaxLife > flameConversion.MaximumLife);
                Assert.True(damagedLife == flame.Monster.Life,
                    $"damagedLife={damagedLife}, currentLife={flame.Monster.Life}, maxLife={flame.Monster.MaxLife}, " +
                    $"maximumShield={flame.MaximumShield}, shield={flame.Shield}, ownerLife={flesh.PlayerLife}");
                Assert.Equal(0u, flame.MaximumShield);
                Assert.Equal(0u, flame.Shield);

                // Removing the source before the dead actor's next deadline permits a healthy revive while the gem is
                // still valid. Re-wearing the source clamps the now-full ordinary pool without granting a full ES pool.
                flame.Monster.Life = flame.Monster.MaxLife;
                fleshSchedule.At = 10.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At);
                var healthyChaos = Assert.Single(flesh.Golems.All.Values,
                    golem => golem.Definition.Kind == "Chaos" && golem.Id != chaosId && golem.Id != revivedChaos.Id);
                Assert.False(healthyChaos.Monster.Dead);
                Assert.True(healthyChaos.Monster.Life > 0);
                fleshInventory.Add(PlayerInventories.BodyArmour, 0, 0, fleshArmour);
                RecalculateFlesh();
                fleshSchedule.At = 11;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, flesh, fleshSchedule.At);
                Assert.Same(flame, flesh.Golems.All[flameId]);
                Assert.Equal(flameConversion.MaximumLife, flame.Monster.MaxLife);
                Assert.Equal(flameConversion.MaximumLife, flame.Monster.Life);
                Assert.True(flame.MaximumShield > 0);
                Assert.Equal(flame.MaximumShield / 6, flame.Shield);
                Assert.True(flame.Shield < flame.MaximumShield);

                // Frozen-stat audit and independent arithmetic controls: B=1000, LifeInc=50, ESInc=100 and 40%
                // converted currently give Life=900 and ES=1000, not convertedES=1200 from a rounded final Life.
                uint minionES = Program.Stats.Key("minion_maximum_energy_shield_+%")!.Value;
                uint minionESFinal = Program.Stats.Key("energy_shield_+%_final_for_minion")!.Value;
                uint noArmourOrES = Program.Stats.Key("minions_have_no_armour_or_energy_shield")!.Value;
                uint activeESFinal = Program.Stats.Key("active_skill_minion_energy_shield_+%_final")!.Value;
                var mappedPlayer = new Dictionary<uint, int>(flame.Player)
                {
                    [minionES] = 100, [minionESFinal] = 100, [noArmourOrES] = 1,
                };
                var mappedEffects = Game.GolemModifiers(flesh, flame, mappedPlayer).Effects;
                Assert.Equal(100, mappedEffects["maximum_energy_shield_+%"]);
                Assert.Equal(100, mappedEffects["energy_shield_+%_final_for_minion"]);
                Assert.Equal(1, mappedEffects["minions_have_no_armour_or_energy_shield"]);

                int baseLife = Game.Monsters.BaseLife(flameVariety, 70) ?? 100;
                var control = new GolemState.Golem(0, null!, flameDefinition with { IsGolem = false }, 0)
                {
                    Gear = new Dictionary<string, int> { ["base_maximum_life"] = 1000 - baseLife },
                    Own = new Dictionary<uint, int> { [activeESFinal] = 0 },
                };
                var controlPlayer = new Dictionary<uint, int>
                    { [Program.Stats.Key("minion_maximum_life_+%")!.Value] = 50 };
                Game.GolemPools.Result Control(IReadOnlyDictionary<string, int> effects,
                    Dictionary<uint, int> own, Dictionary<string, int>? auras = null)
                {
                    control.Own = new(own);
                    control.Auras = auras ?? [];
                    return Game.GolemPools.Calculate(Game.Monsters, flameVariety, 70, control, controlPlayer,
                        new MonsterModEffects(effects));
                }
                control.Own = new()
                {
                    [minionESFinal] = 10, [noArmourOrES] = 1, [activeESFinal] = 25,
                };
                var additiveMinionEffects = Game.GolemModifiers(flesh, control, mappedPlayer).Effects;
                Assert.Equal(110, additiveMinionEffects["energy_shield_+%_final_for_minion"]);
                Assert.Equal(2, additiveMinionEffects["minions_have_no_armour_or_energy_shield"]);
                Assert.Equal(25, additiveMinionEffects["active_skill_minion_energy_shield_+%_final"]);
                var mappedNoShield = Game.GolemPools.Calculate(Game.Monsters, flameVariety, 70, control,
                    controlPlayer, additiveMinionEffects);
                Assert.Equal(0, mappedNoShield.MaximumEnergyShield);
                var converted = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                    ["maximum_energy_shield_+%"] = 100,
                }, []);
                Assert.Equal(1000, converted.RawBaseLife);
                Assert.Equal(40, converted.ConversionPercent);
                Assert.Equal(900u, converted.MaximumLife);
                Assert.Equal(1000, converted.MaximumEnergyShield);
                var ordinaryGain = Control(new Dictionary<string, int>
                    { ["maximum_life_%_to_add_as_maximum_energy_shield"] = 15 }, []);
                Assert.Equal(225, ordinaryGain.MaximumEnergyShield);
                var gainAndConvert = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_add_as_maximum_energy_shield"] = 15,
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                }, []);
                Assert.Equal(825, gainAndConvert.MaximumEnergyShield);
                var more = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_add_as_maximum_energy_shield"] = 15,
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                    ["energy_shield_+%_final_for_minion"] = 100,
                }, new() { [Program.Stats.Key("active_skill_minion_life_+%_final")!.Value] = 100,
                    [activeESFinal] = 100 });
                Assert.Equal(6600, more.MaximumEnergyShield);
                var zeroLifeMore = Control(new Dictionary<string, int>(), new()
                {
                    [Program.Stats.Key("active_skill_minion_life_+%_final")!.Value] = -100,
                });
                Assert.Equal(0, zeroLifeMore.LifeMore);
                Assert.Equal(1u, zeroLifeMore.MaximumLife);
                var flat = Control(new Dictionary<string, int> { ["maximum_energy_shield_+%"] = 100 },
                    [], new() { ["base_maximum_energy_shield"] = 100 });
                Assert.Equal(200, flat.MaximumEnergyShield);
                control.Gear = new Dictionary<string, int> { ["base_maximum_life"] = int.MaxValue };
                var wideFlatLife = Control(new Dictionary<string, int>(), []);
                Assert.Equal((double)baseLife + int.MaxValue, wideFlatLife.RawBaseLife);
                Assert.Equal((uint)int.MaxValue, wideFlatLife.MaximumLife);
                Assert.True(double.IsFinite(wideFlatLife.MaximumEnergyShield));
                control.Gear = new Dictionary<string, int> { ["base_maximum_life"] = 1000 - baseLife };
                var noShield = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                    ["minions_have_no_armour_or_energy_shield"] = 1,
                }, []);
                Assert.Equal(0, noShield.MaximumEnergyShield);
                var negativeChaos = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                    ["base_chaos_damage_resistance_%"] = -100,
                }, []);
                Assert.True(negativeChaos.ChaosResistance < 0);
                Assert.Equal(0, negativeChaos.ConversionPercent);
                Assert.Equal(1500u, negativeChaos.MaximumLife);
                var cappedChaos = Control(new Dictionary<string, int>
                {
                    ["maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"] = 2,
                    ["base_chaos_damage_resistance_%"] = int.MaxValue,
                    ["base_maximum_chaos_damage_resistance_%"] = int.MaxValue,
                }, []);
                Assert.Equal(90, cappedChaos.ChaosResistance);
                Assert.True(cappedChaos.Fatal);
                Assert.Equal(0u, cappedChaos.MaximumLife);
            }
            finally
            {
                Game.AreaUpdateContext.Value = previousFleshContext;
                if (flesh is not null)
                {
                    flesh.Closed = true;
                    PlayerInventories.Forget(fleshCharacter);
                    Game.InactiveItems.TryRemove(fleshCharacter, out _);
                }
            }

            // The SQL correction replaces the imported Fleshcrafter row once and preserves later administrator edits.
            var catalogPath = Path.Combine(Path.GetTempPath(), $"localexile-catalog-{Guid.NewGuid():N}.db");
            try
            {
                using var store = new GameServer.Storage.GameDataStore(catalogPath);
                static string UniqueKey(UniqueCatalog.Definition entry) => $"{entry.Name}|{entry.BaseTypeId}";
                GameDataCatalog.Reload(store);
                var correctedFlesh = GameDataCatalog.Uniques.Single(entry => entry.Name == "Fleshcrafter");
                Assert.Contains("MinionLifeConvertedToEnergyShieldUnique__1", correctedFlesh.Mods);

                string fleshKey = UniqueKey(correctedFlesh);
                using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={catalogPath}"))
                {
                    db.Open();
                    using var update = db.CreateCommand();
                    update.CommandText = "UPDATE unique_items SET item_level = 84 WHERE name = $name AND base_type = $base";
                    update.Parameters.AddWithValue("$name", correctedFlesh.Name);
                    update.Parameters.AddWithValue("$base", correctedFlesh.BaseTypeId);
                    Assert.Equal(1, update.ExecuteNonQuery());
                }
                GameServer.Storage.SqlUpdater.Update(catalogPath, "world", log: _ => { });
                GameDataCatalog.Reload(store);
                Assert.Equal(84, GameDataCatalog.Uniques.Single(entry => UniqueKey(entry) == fleshKey).ItemLevel);
            }
            finally
            {
                GameDataCatalog.Reload(GameData.Store!);
            }

            var criticalMods = MonsterModSet.None with
            {
                Effects = new(new Dictionary<string, int>
                {
                    ["critical_strike_chance_+%"] = 100,
                    ["spell_critical_strike_chance_+%"] = 100,
                }),
            };
            var criticalAttack = actualCriticalAbility with { Crit = 500, Attack = actualCriticalAbility.Attack with { Spell = false } };
            var criticalSpell = actualCriticalAbility with { Crit = 500, Attack = actualCriticalAbility.Attack with { Spell = true } };
            Assert.Equal(1_000, Game.GolemCriticalChance(criticalAttack, criticalMods));
            Assert.Equal(1_500, Game.GolemCriticalChance(criticalSpell, criticalMods));
            Assert.Equal(10_000, Game.GolemCriticalChance(criticalAttack, MonsterModSet.None with
            {
                Effects = new(new Dictionary<string, int> { ["critical_strike_chance_+%"] = int.MaxValue }),
            }));
            Assert.False(Game.GolemCriticalStrike(criticalAttack, MonsterModSet.None with
            {
                Effects = new(new Dictionary<string, int> { ["never_crit"] = 1, ["always_crit"] = 1 }),
            }, 0));
            Assert.True(Game.GolemCriticalStrike(criticalAttack, MonsterModSet.None with
            {
                Effects = new(new Dictionary<string, int> { ["always_crit"] = 1 }),
            }, 9_999));

            inventory.Remove(PlayerInventories.Helmet, inventory.Items(PlayerInventories.Helmet).Single().Id);
            conn.PlayerStats.Remove(maximumPowerCharges);
            Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot());
            conn.Pools = Game.PoolsFor(conn, conn.Pools.Level);

            uint worn = Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id;
            int experience = Program.Items.Gem(Program.Items.ByHash(gem.TypeHash)!)!.ExperienceFor(20);
            Game.ItemsOf(conn).Change(PlayerInventories.Weapon1, worn, item => item with
            { Sockets = [new(SocketColour.White, gem with { GemExperience = experience })] });
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 9);
            Assert.Equal(70, conn.Golems.All.Values.Single().Monster.Level);
            Assert.Equal(29, conn.Golems.OwnerStats[increased]);
            var auraFighter = conn.Golems.All.Values.Single();
            var auraAt = auraFighter.Monster.Walk.Position(0);
            var allyHaste = new AuraCatalog.Aura(1, Program.Skills.EffectRow("Haste"), "Haste", "Haste", 1, 152, 0, 5000);
            var ally = new InstancePlayer(0x2000abcd, 0, new("Ally", 0, auraAt.X, auraAt.Y, 100, 100), auraAt.X, auraAt.Y, 100)
                { Auras = [new(allyHaste, 1000, 100)] };
            conn.Instance.PublishPlayer(ally);
            await Game.SendOwnAuraChanges("test", wire.Stream, wire.Ciphers, conn);
            Assert.Single(conn.ReceivedAuras);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 9.6);
            Assert.Equal(20, auraFighter.Auras["base_movement_velocity_+%"]);
            Assert.Equal(30, auraFighter.Auras["attack_speed_+%"]);
            conn.Instance.RemovePlayer(ally.Id);
            await Game.SendOwnAuraChanges("test", wire.Stream, wire.Ciphers, conn);
            Assert.Empty(conn.ReceivedAuras);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 10.2);
            Assert.Empty(auraFighter.Auras); // Leaving the aura's instance retires all its cached minion bonuses.
            var living = conn.Golems.All.Values.Single();
            var position = living.Monster.Walk.Position(10.3);
            var target = new SpawnedMonster("golem target", new(position.X + 2, position.Y, 0), 100, 8, 1000000);
            conn.Monsters[0x08000001] = target;
            var schedule = new AreaClockSchedule();
            var updates = new PlayerUpdatesState(lifetime, schedule);
            var previousAreaUpdateContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = updates;
            try
            {
                for (int step = 0; step <= 16 && target.Life == target.MaxLife; step++)
                {
                    schedule.At = 10.3 + step / 10d;
                    await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, schedule.At);
                }
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousAreaUpdateContext;
            }
            Assert.True(target.Life < target.MaxLife);
            var smallHit = spell with { Damage = DamagePacket.Of(DamageType.Physical, 100, 100) };
            uint before = living.Monster.Life;
            Assert.True(Game.GolemTakeHitLocked(conn, living, 0x08000000, attacker, smallHit, 12));
            Assert.True(living.Monster.Life < before);

            // A death reported by another damage executor still schedules the same two-second return.
            attacker.BusyUntil = double.PositiveInfinity; attacker.ActionGeneration = 1;
            conn.Monsters[0x08000000] = attacker;
            conn.Golems.IncomingHits.Add(new(0x08000000, attacker, living, spell, 1));
            living.Monster.Dead = true; living.Monster.Life = 0;
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 12);
            Assert.Equal(14, living.ReviveAt);
            Assert.Equal(12, attacker.BusyUntil); Assert.Empty(conn.Golems.IncomingHits);

            Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 13);
            Assert.Empty(conn.Golems.All); Assert.Empty(conn.Golems.Buffs);
            Assert.False(conn.Golems.OwnerStats.ContainsKey(increased));
            await Game.GolemsTick("test", wire.Stream, wire.Ciphers, conn, 15);
            Assert.Empty(conn.Golems.All);

            // Reservation minions use the same native abilities/contact pipeline, without acquiring golem limits,
            // owner buffs or the local golem auto-resummon rule. Every permanent gem must release its reservation.
            foreach (string effectId in new[] { "Haste", "PurityOfElements", "PurityOfFire", "PurityOfIce", "PurityOfLightning",
                "Clarity", "Vitality", "Precision", "Determination", "Grace", "Discipline", "Anger", "Hatred", "Wrath",
                "Zealotry", "Malevolence", "BloodAndSand", "FleshAndStone", "Pride", "ArcticArmour", "TempestShield",
                "HeraldOfAsh", "HeraldOfIce", "HeraldOfThunder", "HeraldOfPurity", "HeraldOfAgony",
                "SummonSkitterbots", "TemporalRift" })
            {
                var type = Program.Items.All.FirstOrDefault(t => Program.Items.Gem(t) is { IsSupport: false } data
                    && Program.Skills.EffectId(data.Variant(0).Effect) == effectId);
                Assert.True(type is not null, "Missing reservation gem: " + effectId);
                var reservationGem = ItemCatalog.Create(type!);
                var wornWeapon = weapon with { Sockets = [new(SocketColour.White, reservationGem)] };
                Game.ItemsOf(conn).Add(PlayerInventories.Weapon1, 0, 0, wornWeapon);
                var active = Game.GemSkillEffects(conn).Single(g => Program.Skills.EffectId(g.Effect) == effectId);
                var reservation = Game.SupportedReservationCost(conn, active.Id);
                Assert.NotNull(reservation);
                if (StanceCatalog.IsSource(effectId))
                {
                    Assert.True(Game.TryBeginSkillCooldownUse(conn, active.Id, active.Effect,
                        Game.CooldownFor(conn, active.Effect, active.Level, active.Quality), instant: true,
                        () => { }, out var stanceUse));
                    Assert.True(await Game.CastGemSkill("test", wire.Stream, wire.Ciphers, conn, active.Id,
                        (0, 0), (0, 0), cooldownUse: stanceUse), effectId);
                }
                else
                    Assert.True(await Game.ToggleAura("test", wire.Stream, wire.Ciphers, conn, active.Id, active.Effect,
                        active.Name, reservation!.Value, active.Level, active.Quality), effectId);
                Assert.Single(conn.Auras); Assert.True(Game.ReservedMana(conn) > 0, effectId);
                var aura = conn.Auras[active.Id];
                if (effectId is "TempestShield" or "TemporalRift")
                {
                    Assert.NotNull(aura.SourceBuff);
                    byte[] expectedList = Convert.FromHexString(effectId == "TempestShield"
                        ? "000000020000000000000000" : "000000020000002A00000064");
                    var activationPackets = AuraCatalog.StartPackets(aura, 0x1000, null).ToArray();
                    Assert.Equal(2, activationPackets.Length);
                    // Both source and received buffs instantiate the same unchecked native behaviour.
                    foreach (var packet in activationPackets)
                        Assert.Equal(expectedList, packet[^12..]);
                }
                if (effectId is "HeraldOfIce" or "HeraldOfThunder" or "TempestShield")
                    Assert.True(Game.ReservationHitPacket(conn, aura).MaxTotal > 0, effectId);
                if (effectId == "HeraldOfPurity")
                {
                    Game.ReservationKill(new(conn, target, "test", position));
                    await Game.ReservationSkillsTick("test", wire.Stream, wire.Ciphers, conn, conn.Clock.Elapsed.TotalSeconds);
                    Assert.Contains(conn.Reservations.Fighters.Values, f => f.Definition.Kind == "Purity");
                }
                if (effectId == "SummonSkitterbots")
                {
                    await Game.ReservationSkillsTick("test", wire.Stream, wire.Ciphers, conn, conn.Clock.Elapsed.TotalSeconds);
                    Assert.Equal(2, conn.Reservations.Fighters.Count);
                }
                await Game.DisableReservations("test", wire.Stream, wire.Ciphers, conn, effectId);
                Assert.Empty(conn.Auras); Assert.Equal(0, Game.ReservedMana(conn));
                Assert.Empty(conn.Reservations.Fighters);
                Game.ItemsOf(conn).Remove(PlayerInventories.Weapon1, Game.ItemsOf(conn).Items(PlayerInventories.Weapon1).Single().Id);
            }

            // Independent integer-recovery controls use the existing host kernel directly. Ordinary recharge is one
            // third of maximum per second after its delay; fractional progress is retained across split steps.
            var ordinaryKernel = new EnergyShield();
            ordinaryKernel.SetMaximum(300, refill: true);
            Assert.Equal(0, ordinaryKernel.Damage(100, bypass: false, now: 1));
            Assert.Equal(200u, ordinaryKernel.Current);
            ordinaryKernel.Advance(2.9, 3.1, _ => 0, alive: true);
            Assert.Equal(210u, ordinaryKernel.Current);
            Assert.Equal(4 / 3d, EnergyShield.Delay(name => name == "energy_shield_delay_-%" ? 50 : 0), 12);
            Assert.Equal(1d, EnergyShield.Delay(name => name == "energy_shield_delay_-%" ? 100 : 0), 12);

            var boundaryKernel = new EnergyShield();
            boundaryKernel.SetMaximum(300, refill: true);
            boundaryKernel.Damage(100, bypass: false, now: 1);
            boundaryKernel.Advance(1, 3, _ => 0, alive: true);
            Assert.Equal(200u, boundaryKernel.Current);
            boundaryKernel.Advance(3, 3.1, _ => 0, alive: true);
            Assert.Equal(210u, boundaryKernel.Current);

            var splitKernel = new EnergyShield();
            splitKernel.SetMaximum(3, refill: true);
            splitKernel.Damage(2, bypass: false, now: 0);
            splitKernel.Advance(0, 2.5, _ => 0, alive: true);
            splitKernel.Advance(2.5, 3, _ => 0, alive: true);
            var combinedKernel = new EnergyShield();
            combinedKernel.SetMaximum(3, refill: true);
            combinedKernel.Damage(2, bypass: false, now: 0);
            combinedKernel.Advance(0, 3, _ => 0, alive: true);
            Assert.Equal(combinedKernel.Current, splitKernel.Current);
            Assert.Equal(2u, splitKernel.Current);

            var interruptedKernel = new EnergyShield();
            interruptedKernel.SetMaximum(300, refill: true);
            interruptedKernel.Damage(100, bypass: false, now: 1);
            interruptedKernel.Advance(1, 3.1, _ => 0, alive: true);
            Assert.Equal(210u, interruptedKernel.Current);
            interruptedKernel.Damage(10, bypass: false, now: 3.1);
            interruptedKernel.Advance(3.1, 5, _ => 0, alive: true);
            Assert.Equal(200u, interruptedKernel.Current);

            // A later ordinary hit owns a newer interruption than a DoT endpoint that expired before it.
            var monotonicInterrupt = new EnergyShield();
            monotonicInterrupt.SetMaximum(300, refill: true);
            monotonicInterrupt.Damage(100, bypass: false, now: 1.5);
            monotonicInterrupt.Interrupt(1.4);
            monotonicInterrupt.Interrupt(double.NaN);
            monotonicInterrupt.Interrupt(double.PositiveInfinity);
            monotonicInterrupt.Advance(1.5, 3.4, _ => 0, alive: true);
            Assert.Equal(200u, monotonicInterrupt.Current);
            monotonicInterrupt.Advance(3.4, 3.5, _ => 0, alive: true);
            Assert.Equal(200u, monotonicInterrupt.Current);
            monotonicInterrupt.Advance(3.5, 3.6, _ => 0, alive: true);
            Assert.Equal(210u, monotonicInterrupt.Current);

            var snapshotKernel = new EnergyShield();
            snapshotKernel.SetMaximum(300, refill: true);
            snapshotKernel.Damage(100, bypass: false, now: 100);
            snapshotKernel.Gain(.5);
            var kernelSnapshot = snapshotKernel.Capture(100.5);
            Assert.Equal(200u, kernelSnapshot.Current);
            Assert.Equal(.5, kernelSnapshot.Fraction, 12);
            Assert.Equal(.5, kernelSnapshot.InterruptedSecondsAgo!.Value, 12);

            var splitRestore = new EnergyShield();
            splitRestore.SetMaximum(300);
            splitRestore.Restore(kernelSnapshot, 500);
            splitRestore.Advance(500, 500, _ => 0, alive: true);
            Assert.Equal(200u, splitRestore.Current);
            splitRestore.Advance(500, 501.5, _ => 0, alive: true);
            Assert.Equal(200u, splitRestore.Current);
            Assert.Equal(.5, splitRestore.Capture(501.5).Fraction, 12);
            splitRestore.Advance(501.5, 501.515625, _ => 0, alive: true);
            var combinedRestore = new EnergyShield();
            combinedRestore.SetMaximum(300);
            combinedRestore.Restore(kernelSnapshot, 500);
            combinedRestore.Advance(500, 501.515625, _ => 0, alive: true);
            Assert.Equal(202u, splitRestore.Current);
            Assert.Equal(combinedRestore.Current, splitRestore.Current);
            var repeatedSnapshot = splitRestore.Capture(501.515625);
            var repeatedRestore = new EnergyShield();
            repeatedRestore.SetMaximum(300);
            repeatedRestore.Restore(repeatedSnapshot, 900);
            Assert.Equal(repeatedSnapshot.Current, repeatedRestore.Current);
            Assert.Equal(.0625, repeatedSnapshot.Fraction, 12);
            Assert.Equal(2.015625, repeatedSnapshot.InterruptedSecondsAgo!.Value, 12);

            var futureSkew = new EnergyShield();
            futureSkew.SetMaximum(300);
            futureSkew.Restore(new(100, .25, -1), 10);
            var futureSnapshot = futureSkew.Capture(10);
            Assert.Equal(.25, futureSnapshot.Fraction, 12);
            Assert.Equal(-1, futureSnapshot.InterruptedSecondsAgo);

            var invalidSnapshot = new EnergyShield();
            invalidSnapshot.SetMaximum(300);
            invalidSnapshot.Restore(new(100, double.NaN, double.NaN), 20);
            var invalidCapture = invalidSnapshot.Capture(20);
            Assert.Equal(0, invalidCapture.Fraction);
            Assert.Equal(0d, invalidCapture.InterruptedSecondsAgo!.Value);
            invalidSnapshot.Advance(20, 21.999, _ => 0, alive: true);
            Assert.Equal(100u, invalidSnapshot.Current);

            var fullSnapshot = new EnergyShield();
            fullSnapshot.SetMaximum(300);
            fullSnapshot.Restore(new(999, .75, null), 30);
            Assert.Equal(300u, fullSnapshot.Current);
            Assert.Equal(0, fullSnapshot.Capture(30).Fraction);
            var zeroCapSnapshot = new EnergyShield();
            zeroCapSnapshot.SetMaximum(0);
            zeroCapSnapshot.Restore(new(100, .5, 0), 40);
            Assert.Equal(0u, zeroCapSnapshot.Current);
            Assert.Equal(2, EnergyShield.Delay(_ => 0));

            var fractionalIgnite = new MonsterIgnites();
            Assert.True(fractionalIgnite.Add(0, .1, .5));
            Assert.Equal(0, fractionalIgnite.Advance(.5, 1, 0, out double? exactIgniteEnd));
            Assert.Equal(.5, exactIgniteEnd);

            var fractionalPoison = new MonsterPoisons();
            Assert.True(fractionalPoison.Add(0, .1, .5));
            Assert.Equal(0, fractionalPoison.Advance(.5, 1, out double? exactPoisonEnd));
            Assert.Equal(.5, exactPoisonEnd);

            var immuneIgnite = new MonsterIgnites();
            Assert.True(immuneIgnite.Add(0, 100, 2));
            Assert.Equal(0, immuneIgnite.Advance(1, 0, 0, out double? immuneEnd));
            Assert.Null(immuneEnd);
            var immunePoison = new MonsterPoisons();
            Assert.True(immunePoison.Add(0, 100, 2));
            Assert.Equal(0, immunePoison.Advance(1, 0, out double? immunePoisonEnd));
            Assert.Null(immunePoisonEnd);

            var straddlingIgnite = new MonsterIgnites();
            Assert.True(straddlingIgnite.Add(0, 2, .5));
            Assert.Equal(1, straddlingIgnite.Advance(1, 1, 0, out double? expiryEnd));
            Assert.Equal(.5, expiryEnd);

            var splitIgnite = new MonsterIgnites();
            Assert.True(splitIgnite.Add(0, 1.2, 1));
            Assert.Equal(0, splitIgnite.Advance(.4, 1));
            Assert.Equal(1, splitIgnite.Advance(1, 1));
            var combinedIgnite = new MonsterIgnites();
            Assert.True(combinedIgnite.Add(0, 1.2, 1));
            Assert.Equal(1, combinedIgnite.Advance(1, 1));

            // C3A isolated authority: exercise the three public manual gem mutation handlers against one real
            // socket host, held behind the same cipher send gate as an actual owned replacement. This fixture ends
            // before the existing IntegerResourceAuthority control and owns all of its state and cleanup.
            const string c3aCharacter = "AtomicGemSourceAuthority";
            var c3aSchedule = new AreaClockSchedule();
            var c3aUpdates = new PlayerUpdatesState(lifetime, c3aSchedule);
            var previousC3AContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = c3aUpdates;
            ClientState? c3a = null;
            ClientState? c3aRestored = null;
            ClientState? c3aZombieRestored = null;
            try
            {
                c3aSchedule.At = 40;
                var c3aSelf = c3a = new ClientState(
                    new(1, "atomic-gem-source-authority", 1000, 1000, 100, 100, null, []), 8, 1)
                {
                    Character = c3aCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                    PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = c3aUpdates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                c3aSelf.Golems.Restored = true;
                c3aSelf.Summons.Restored = true;
                foreach (string stat in new[]
                         {
                             "virtual_strength_for_requirements", "virtual_dexterity_for_requirements",
                             "virtual_intelligence_for_requirements",
                         })
                    c3aSelf.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                c3aSelf.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                c3aSelf.PlayerStats[Program.Stats.Key("minion_life_regeneration_rate_per_minute_%")!.Value] = -60;
                c3aSelf.PlayerStats[Program.Stats.Key("minion_additional_spell_block_%")!.Value] = 0;
                c3aSelf.PlayerStats[Program.Stats.Key("minion_block_%")!.Value] = 0;
                c3aSelf.PlayerStats[Program.Stats.Key("minion_physical_damage_reduction_rating")!.Value] = 0;
                c3aSelf.PlayerStats[Program.Stats.Key("golem_physical_damage_reduction_rating")!.Value] = 0;
                c3aSelf.IncomingHitRandom = new LowestRandom();

                var c3aInventory = global::Game.ItemsOf(c3aSelf);
                var c3aTreeRoll = Program.Items.Mods.ById(
                    "WeaponTreeMinionLifeAsExtraEnergyShieldReducedElementalResistance1")!
                    .Roll(ModSection.Explicit, new Random(504));
                Assert.Equal(15, c3aTreeRoll.Values[0]);
                var c3aBow = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!);
                var c3aFlameGem = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
                var c3aRockGem = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Gems/SkillGemSummonRockGolem")!);
                Assert.Equal(1, global::Game.ItemsFor(c3aSelf).GemLevel(c3aFlameGem));
                var c3aWeaponSlot = c3aInventory.Add(PlayerInventories.Weapon1, 0, 0, c3aBow with
                {
                    Sockets = [new(SocketColour.White, c3aFlameGem)],
                    Mods = [.. c3aBow.Mods, c3aTreeRoll],
                });
                var c3aFlesh = new UniqueCatalog(Program.Items).Create("Fleshcrafter", new Random(504))!;
                Assert.Contains(c3aFlesh.Mods, mod => Program.Items.Mods.ByHash(mod.Hash)?.Id ==
                    "MinionLifeConvertedToEnergyShieldUnique__1");
                c3aInventory.Add(PlayerInventories.BodyArmour, 0, 0, c3aFlesh);
                void c3aRecalculate()
                {
                    global::Game.EquipmentStatSources(c3aSelf, c3aInventory.EquipmentSnapshot());
                    c3aSelf.Pools = global::Game.PoolsFor(c3aSelf, c3aSelf.Pools.Level);
                    c3aSelf.PlayerLife = c3aSelf.Pools.Life;
                    c3aSelf.PlayerMana = c3aSelf.Pools.Mana;
                }
                c3aRecalculate();

                Dictionary<uint, uint>? PlayerStatsFrom(IEnumerable<byte[]> packets)
                {
                    var header = InstanceEntities.StatsUpdate(Program.PlayerId, []).AsSpan(0, 12);
                    Dictionary<uint, uint>? found = null;
                    foreach (var packet in packets)
                    {
                        int offset = packet.AsSpan().IndexOf(header);
                        if (offset < 0) continue;
                        int cursor = offset + header.Length;
                        uint Compact()
                        {
                            byte first = packet[cursor++];
                            int following = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2
                                : first < 0xf0 ? 3 : 4;
                            uint value = following == 4 ? 0u : (uint)(first & (0x7f >> following));
                            for (int i = 0; i < following; i++) value = (value << 8) | packet[cursor++];
                            return value;
                        }
                        uint count = Compact();
                        found = [];
                        for (uint i = 0; i < count; i++) found[Compact()] = Compact();
                    }
                    return found;
                }

                var c3aSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                var c3aFlameSourceKey = new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aSource.Effect);
                var c3aDefinition = global::Game.GolemData.Find("SummonFlameGolem")!;
                var c3aInitialPackets = new List<byte[]>();
                var c3aBeforeInitialOutgoing = Program.Outgoing;
                Program.Outgoing = (ciphers, bytes) =>
                {
                    if (ReferenceEquals(ciphers, wire.Ciphers)) lock (c3aInitialPackets) c3aInitialPackets.Add(bytes.ToArray());
                    c3aBeforeInitialOutgoing?.Invoke(ciphers, bytes);
                };
                uint c3aInitialId;
                try
                {
                    c3aInitialId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aSource.Id, c3aDefinition, (100, 100));
                }
                finally { Program.Outgoing = c3aBeforeInitialOutgoing; }
                Assert.NotEqual(0u, c3aInitialId);
                var c3aInitial = Assert.Single(c3aSelf.Golems.All).Value;
                var c3aTotalGolems = Program.Stats.Key("virtual_total_number_of_active_golems")!.Value;
                var c3aFireGolems = Program.Stats.Key("current_number_of_fire_golems")!.Value;
                var c3aOwnerDamage = Program.Stats.Key("damage_+%")!.Value;
                int c3aOwnerDamageValue = c3aInitial.OwnerBuff[c3aOwnerDamage];
                Assert.True(c3aOwnerDamageValue > 0);
                var c3aInitialStats = PlayerStatsFrom(c3aInitialPackets);
                Assert.NotNull(c3aInitialStats);
                Assert.Equal(1u, c3aInitialStats[c3aTotalGolems]);
                Assert.Equal(1u, c3aInitialStats[c3aFireGolems]);
                Assert.Equal((uint)c3aOwnerDamageValue, c3aInitialStats[c3aOwnerDamage]);
                using (new global::Game.PlayerStatScope(c3aSelf, fresh: true))
                    Assert.Contains(global::Game.PlayerSkills(c3aSelf), skill =>
                        skill.Stats?.Any(stat => stat.Key == c3aOwnerDamage && stat.Value == c3aOwnerDamageValue) == true);
                int c3aInitialActorLevel = global::Game.SupportedSkill(c3aSelf, c3aSource.Id).Stats.GetValueOrDefault(
                    Program.Stats.Key("base_display_minion_actor_level")!.Value);
                Assert.Equal(c3aInitialActorLevel, c3aInitial.Monster.Level);
                Assert.True(c3aInitialActorLevel > 1);
                Assert.True(c3aInitial.MaximumShield > 2);

                // One actual physical spell with zero spell block and zero physical mitigation establishes a partial
                // ES pool while leaving Life intact, without the Fire damage rounding loop.
                var c3aHitSource = new SpawnedMonster("atomic gem physical spell", new(100, 100, 1), 100, 8, 1_000_000);
                int c3aHitAmount = checked((int)(c3aInitial.Shield / 2) + 1);
                var c3aPhysicalHit = new MonsterAttack(0x4000, 1, 100,
                    DamagePacket.Of(DamageType.Physical, c3aHitAmount, c3aHitAmount)) { Spell = true };
                uint c3aLifeBeforeHit = c3aInitial.Monster.Life;
                uint c3aShieldBeforeHit = c3aInitial.Shield;
                using (global::Game.ProjectileMutation(c3aSelf, advance: false, phase: "c3a physical spell"))
                    Assert.True(global::Game.GolemTakeHitLocked(c3aSelf, c3aInitial, 0x09000041, c3aHitSource,
                        c3aPhysicalHit, c3aSchedule.At), "the actual physical spell did not reach the golem");
                Assert.Equal(c3aLifeBeforeHit, c3aInitial.Monster.Life);
                uint c3aPartialShield = c3aInitial.Shield;
                Assert.True(c3aPartialShield > 0);
                Assert.True(c3aPartialShield < c3aShieldBeforeHit);
                // Neutral fractional status control: this is not a hostile admission.
                Assert.True(c3aInitial.Monster.Ignites.Add(c3aSchedule.At, .25, 10));
                double c3aStatusUntil = c3aInitial.Monster.Ignites.Until;
                uint c3aPreservedLife = c3aInitial.Monster.Life;

                // Normal production XP, not a raw XP setter: Standard's null multiplier makes ten monster XP one
                // whole gem XP. XP stops below the next threshold; the actual level-up batch then changes the
                // source to level 2 while a fresh level-1 create waits, so its captured pools cannot commit. The real tick rebuilds the owned golem while
                // retaining absolute resources and status.
                var c3aGemType = global::Game.ItemsFor(c3aSelf).Gem(
                    global::Game.ItemsFor(c3aSelf).ByHash(c3aFlameGem.TypeHash)!)!;
                var c3aSocketGem = c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!;
                int c3aLevelTwoExperience = c3aGemType.ExperienceFor(2);
                Assert.True(c3aLevelTwoExperience > c3aSocketGem.GemExperience);
                Assert.Null(c3aSelf.LeagueModule.Rules.GemExperienceMultiplier);
                uint c3aGain = checked((uint)(c3aLevelTwoExperience - c3aSocketGem.GemExperience) * 10);
                var c3aNormalXpToken = c3aSelf.GemSourceLifetimes.Current(c3aFlameSourceKey);
                var c3aNormalXpGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aNormalXpGate.Wait(0));
                Task<uint>? c3aStaleLevelOneFresh = null;
                List<byte[]> c3aXpPackets;
                bool c3aLeveled;
                try
                {
                    c3aStaleLevelOneFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aSource.Id, c3aDefinition, (100, 100), arrival: true);
                    Assert.False(c3aStaleLevelOneFresh.IsCompleted,
                        "the actual level-1 fresh golem did not reach the held create gate");
                    var xp = global::Game.CommitGemExperience(c3aSelf, c3aGain);
                    c3aXpPackets = xp.Packets;
                    Assert.False(xp.Leveled);
                    Assert.Equal(c3aLevelTwoExperience - 1,
                        c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!.GemExperience);
                    c3aLeveled = global::Game.LevelUpReadyGems(c3aSelf).Count != 0;
                }
                finally { c3aNormalXpGate.Release(); }
                Assert.Equal(0u, await c3aStaleLevelOneFresh!);
                Assert.True(c3aLeveled);
                Assert.NotEmpty(c3aXpPackets);
                var c3aLevelTwoGem = c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!;
                Assert.Equal(2, global::Game.ItemsFor(c3aSelf).GemLevel(c3aLevelTwoGem));
                Assert.Same(c3aNormalXpToken, c3aSelf.GemSourceLifetimes.Current(c3aFlameSourceKey));
                Assert.Same(c3aInitial, Assert.Single(c3aSelf.Golems.All).Value);
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, c3aSelf, c3aSchedule.At);
                var c3aLevelTwo = Assert.Single(c3aSelf.Golems.All).Value;
                Assert.NotSame(c3aInitial, c3aLevelTwo);
                var c3aLevelTwoSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                int c3aLevelTwoActorLevel = global::Game.SupportedSkill(c3aSelf, c3aLevelTwoSource.Id).Stats.GetValueOrDefault(
                    Program.Stats.Key("base_display_minion_actor_level")!.Value);
                Assert.Equal(c3aLevelTwoActorLevel, c3aLevelTwo.Monster.Level);
                Assert.True(c3aLevelTwoActorLevel > c3aInitialActorLevel);
                Assert.True(c3aLevelTwo.Monster.MaxLife > c3aInitial.Monster.MaxLife);
                Assert.Equal(c3aPreservedLife, c3aLevelTwo.Monster.Life);
                Assert.Equal(c3aPartialShield, c3aLevelTwo.Shield);
                Assert.Equal(c3aStatusUntil, c3aLevelTwo.Monster.Ignites.Until);

                // A replacement prepared from the current level-2 source must reject after the public "+" changes
                // the same source to level 3. The following current tick rebuilds from the new level without healing.
                var c3aLevelSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                var c3aGemTarget = new InstanceItems.SocketGem(
                    PlayerInventories.InventoryId(PlayerInventories.Weapon1), c3aWeaponSlot.Id, 0);
                var c3aLevelGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aLevelGate.Wait(0));
                Task<uint>? c3aStaleLevelTwo = null;
                Task<uint>? c3aStaleLevelTwoFresh = null;
                Task? c3aLevelUp = null;
                try
                {
                    c3aStaleLevelTwo = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aLevelSource.Id, c3aDefinition, (100, 100), arrival: true, replacingOld: c3aLevelTwo);
                    Assert.False(c3aStaleLevelTwo.IsCompleted,
                        "the actual level-2 replacement did not reach the held create gate");
                    c3aStaleLevelTwoFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aLevelSource.Id, c3aDefinition, (100, 100), arrival: true);
                    Assert.False(c3aStaleLevelTwoFresh.IsCompleted,
                        "the actual level-2 fresh golem did not reach the held create gate");
                    c3aLevelUp = global::Game.LevelUpGem("test", wire.Stream, wire.Ciphers, c3aSelf, c3aGemTarget);
                    Assert.False(c3aLevelUp.IsCompleted,
                        "the actual level-up handler did not suspend after its synchronous mutation");
                    var c3aLevelThreeGem = c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!;
                    Assert.Equal(c3aGemType.ExperienceFor(3), c3aLevelThreeGem.GemExperience);
                    Assert.Equal(3, global::Game.ItemsFor(c3aSelf).GemLevel(c3aLevelThreeGem));
                    Assert.Equal(3, global::Game.SupportedSkill(c3aSelf, c3aLevelSource.Id).Level);
                }
                finally
                {
                    c3aLevelGate.Release();
                }
                Assert.Equal(0u, await c3aStaleLevelTwo!);
                Assert.Equal(0u, await c3aStaleLevelTwoFresh!);
                await c3aLevelUp!;
                Assert.Same(c3aNormalXpToken, c3aSelf.GemSourceLifetimes.Current(c3aFlameSourceKey));
                c3aSchedule.At = 40.5;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, c3aSelf, c3aSchedule.At);
                var c3aLevelThree = Assert.Single(c3aSelf.Golems.All).Value;
                Assert.NotSame(c3aLevelTwo, c3aLevelThree);
                var c3aLevelThreeTickSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                int c3aLevelThreeActorLevel = global::Game.SupportedSkill(c3aSelf, c3aLevelThreeTickSource.Id).Stats
                    .GetValueOrDefault(Program.Stats.Key("base_display_minion_actor_level")!.Value);
                Assert.Equal(c3aLevelThreeActorLevel, c3aLevelThree.Monster.Level);
                Assert.True(c3aLevelThreeActorLevel > c3aLevelTwoActorLevel);
                Assert.True(c3aLevelThree.Monster.MaxLife > c3aLevelTwo.Monster.MaxLife);
                Assert.Equal(c3aPreservedLife, c3aLevelThree.Monster.Life);
                Assert.Equal(c3aPartialShield, c3aLevelThree.Shield);
                Assert.Equal(c3aStatusUntil, c3aLevelThree.Monster.Ignites.Until);

                // A real cursor gem with a different effect is swapped in by the public socket handler while a
                // replacement prepared from the old Flame source is held. The old source is gone before output
                // resumes, so the prepared replacement must reject; the old actor lifecycle remains C3B scope.
                c3aInventory.Add(PlayerInventories.Cursor, 0, 0, c3aRockGem);
                var c3aLevelThreeSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                var c3aSocketGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aSocketGate.Wait(0));
                Task<uint>? c3aStaleSocketReplacement = null;
                Task? c3aSocketSwap = null;
                try
                {
                    c3aStaleSocketReplacement = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aLevelThreeSource.Id, c3aDefinition, (100, 100), arrival: true, replacingOld: c3aLevelThree);
                    Assert.False(c3aStaleSocketReplacement.IsCompleted);
                    c3aSocketSwap = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, c3aSelf, c3aGemTarget);
                    Assert.False(c3aSocketSwap.IsCompleted,
                        "the actual socket handler did not suspend after its synchronous mutation");
                    var c3aSwappedSocket = c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!;
                    Assert.Equal(c3aRockGem.TypeHash, c3aSwappedSocket.TypeHash);
                    var c3aHeldFlame = Assert.Single(c3aInventory.Items(PlayerInventories.Cursor));
                    Assert.Equal(c3aFlameGem.TypeHash, c3aHeldFlame.Item.TypeHash);
                    Assert.DoesNotContain(global::Game.GemSkillEffects(c3aSelf),
                        gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                }
                finally
                {
                    c3aSocketGate.Release();
                }
                Assert.Equal(0u, await c3aStaleSocketReplacement!);
                await c3aSocketSwap!;

                // Restore through the same public handler, then remove only the unused cursor fixture and make a
                // fresh actual owned summon from the restored current source.
                await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, c3aSelf, c3aGemTarget);
                var c3aHeldRock = Assert.Single(c3aInventory.Items(PlayerInventories.Cursor));
                Assert.Equal(c3aRockGem.TypeHash, c3aHeldRock.Item.TypeHash);
                Assert.NotNull(c3aInventory.Remove(PlayerInventories.Cursor, c3aHeldRock.Id));
                var c3aRestoredSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                // The restored source has a fresh identity while the pre-swap actor remains C3B retirement scope.
                // Leave that actor untouched and make the required fresh summon under a labelled fixture limit.
                c3aSelf.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 2;
                c3aRecalculate();
                uint c3aFreshId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aRestoredSource.Id, c3aDefinition, (100, 100), arrival: true);
                Assert.NotEqual(0u, c3aFreshId);
                var c3aFresh = c3aSelf.Golems.All[c3aFreshId];
                Assert.Equal(c3aLevelThreeActorLevel, c3aFresh.Monster.Level);

                // The public unsocket removes that current source while another replacement is prepared. The stale
                // create rejects from the fresh source map; no assertion claims the old C3B actor must be retired.
                var c3aFreshSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                var c3aUnsocketGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aUnsocketGate.Wait(0));
                Task<uint>? c3aStaleUnsocketReplacement = null;
                Task? c3aUnsocket = null;
                try
                {
                    c3aStaleUnsocketReplacement = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aFreshSource.Id, c3aDefinition, (100, 100), arrival: true, replacingOld: c3aFresh);
                    Assert.False(c3aStaleUnsocketReplacement.IsCompleted);
                    c3aUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, c3aSelf, c3aGemTarget);
                    Assert.False(c3aUnsocket.IsCompleted,
                        "the actual unsocket handler did not suspend after its synchronous mutation");
                    Assert.Null(c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem);
                    var c3aUnsocketHeld = Assert.Single(c3aInventory.Items(PlayerInventories.Cursor));
                    Assert.Equal(c3aFlameGem.TypeHash, c3aUnsocketHeld.Item.TypeHash);
                    Assert.DoesNotContain(global::Game.GemSkillEffects(c3aSelf),
                        gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                }
                finally
                {
                    c3aUnsocketGate.Release();
                }
                Assert.Equal(0u, await c3aStaleUnsocketReplacement!);
                await c3aUnsocket!;

                // C3B2: remove and restore the exact physical source while a fresh preparation and a replacement
                // are both held at their create gate. The final tuple and skill id are restored, but neither stale
                // preparation may commit against the old physical lifetime and the existing actor retires eagerly.
                await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, c3aSelf, c3aGemTarget);
                c3aSelf.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 4;
                c3aRecalculate();
                var c3aReaddedSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                Assert.True(global::Game.SupportedSkill(c3aSelf, c3aReaddedSource.Id).RequirementsMet);
                uint c3aRemovalOldId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aReaddedSource.Id, c3aDefinition, (100, 100), arrival: true);
                Assert.NotEqual(0u, c3aRemovalOldId);
                var c3aRemovalOld = c3aSelf.Golems.All[c3aRemovalOldId];
                var c3aStoneGem = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Gems/SkillGemSummonRockGolem")!);
                c3aInventory.Change(PlayerInventories.Weapon1, c3aWeaponSlot.Id, item => item with
                {
                    Sockets = [.. item.Sockets, new(SocketColour.White, c3aStoneGem)],
                });
                c3aRecalculate();
                var c3aStoneSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonStoneGolem");
                var c3aStoneDefinition = global::Game.GolemData.Find("SummonStoneGolem")!;
                uint c3aStoneId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aStoneSource.Id, c3aStoneDefinition, (100, 100), arrival: true);
                Assert.NotEqual(0u, c3aStoneId);
                var c3aStone = c3aSelf.Golems.All[c3aStoneId];
                Assert.NotEqual(c3aDefinition.Buff, c3aStoneDefinition.Buff);
                Assert.True(c3aSelf.Golems.Buffs.TryGetValue(c3aDefinition.Buff, out var c3aFlameBuff));
                Assert.True(c3aSelf.Golems.Buffs.TryGetValue(c3aStoneDefinition.Buff, out _));
                var c3aRemovalToken = c3aSelf.GemSourceLifetimes.Current(
                    new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aReaddedSource.Effect));
                Assert.NotNull(c3aRemovalToken);
                // Supplemental neutral fixture: seed the removal-observable collections so cleanup can be checked.
                // These entries do not exercise pending, hazard, pulse, or incoming-hit producers or admission.
                var c3aEnemy = new SpawnedMonster("c3a direct minion source enemy", new(100, 100, 1), 100, 8, 1_000_000);
                const uint c3aEnemyId = 0x0800c3b2;
                c3aSelf.Monsters[c3aEnemyId] = c3aEnemy;
                c3aRemovalOld.Pending = new(c3aRemovalOld.Abilities[0], c3aEnemyId, c3aEnemy, (100, 100),
                    c3aSchedule.At, 0);
                var c3aRemovalHazard = new GolemState.Hazard(1, (100, 100), c3aSchedule.At + 5, 2, 1,
                    DamageType.Physical) { Object = 0x0900c3b2 };
                c3aRemovalOld.Hazards.Add(c3aRemovalHazard);
                c3aRemovalOld.Pulses.Add(new(c3aRemovalOld.Abilities[0], (100, 100), c3aSchedule.At + 5, 1));
                c3aSelf.Golems.IncomingHits.Add(new(c3aEnemyId, c3aEnemy, c3aRemovalOld,
                    new MonsterAttack(1, 1, 100, DamagePacket.Of(DamageType.Physical, 1, 1)), 0));
                c3aSelf.Golems.Waiting.Add(new(new(c3aDefinition.Id, c3aReaddedSource.Id, ReviveSeconds: 5),
                    c3aSchedule.At + 5));
                var c3aStoneGolems = Program.Stats.Key("current_number_of_stone_golems")!.Value;
                long c3aGolemVersion = c3aSelf.Golems.Version;
                long c3aMinionRevision = c3aSelf.Minions.StatRevision;
                var c3aRemovalGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aRemovalGate.Wait(0));
                Task<uint>? c3aStaleFresh = null;
                Task<uint>? c3aStaleReplacement = null;
                Task? c3aRemovalUnsocket = null;
                Task? c3aRemovalSocket = null;
                try
                {
                    c3aStaleFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aReaddedSource.Id, c3aDefinition, (100, 100), arrival: true);
                    c3aStaleReplacement = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aReaddedSource.Id, c3aDefinition, (100, 100), arrival: true, replacingOld: c3aRemovalOld);
                    Assert.False(c3aStaleFresh.IsCompleted);
                    Assert.False(c3aStaleReplacement.IsCompleted);
                    c3aRemovalUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aGemTarget);
                    Assert.False(c3aRemovalUnsocket.IsCompleted);
                    Assert.DoesNotContain(c3aRemovalOldId, c3aSelf.Golems.All.Keys);
                    Assert.DoesNotContain(c3aRemovalOldId, c3aSelf.Monsters.Keys);
                    Assert.False(c3aSelf.Minions.Owns(c3aRemovalOldId, c3aRemovalOld.Monster));
                    Assert.Same(c3aStone, c3aSelf.Golems.All[c3aStoneId]);
                    Assert.Null(c3aRemovalOld.Pending);
                    Assert.Empty(c3aRemovalOld.Hazards);
                    Assert.Empty(c3aRemovalOld.Pulses);
                    Assert.DoesNotContain(c3aSelf.Golems.IncomingHits, hit => ReferenceEquals(hit.Target, c3aRemovalOld));
                    Assert.DoesNotContain(c3aSelf.Golems.Waiting,
                        waiting => waiting.Saved.Skill == c3aReaddedSource.Id);
                    Assert.False(c3aSelf.Golems.Buffs.ContainsKey(c3aDefinition.Buff));
                    Assert.True(c3aSelf.Golems.Buffs.ContainsKey(c3aStoneDefinition.Buff));
                    Assert.Equal(1, c3aSelf.Golems.OwnerStats[c3aTotalGolems]);
                    Assert.False(c3aSelf.Golems.OwnerStats.ContainsKey(c3aFireGolems));
                    Assert.Equal(1, c3aSelf.Golems.OwnerStats[c3aStoneGolems]);
                    Assert.True(c3aSelf.Golems.Version > c3aGolemVersion);
                    Assert.True(c3aSelf.Minions.StatRevision > c3aMinionRevision);
                    using (new global::Game.PlayerStatScope(c3aSelf, fresh: true))
                    {
                        var c3aImmediateStats = global::Game.CharacterPlayerStats(c3aSelf);
                        Assert.Equal(1, c3aImmediateStats[c3aTotalGolems]);
                        Assert.False(c3aImmediateStats.ContainsKey(c3aFireGolems));
                        Assert.Equal(1, c3aImmediateStats[c3aStoneGolems]);
                    }
                    var c3aBuffRemoval = InstanceEntities.RemoveBuff(Program.PlayerId, c3aFlameBuff.Instance);
                    var c3aQueuedBuffRemoval = Assert.Single(c3aSelf.Minions.OutputQueue,
                        output => output.Packet.AsSpan().SequenceEqual(c3aBuffRemoval));
                    Assert.Null(c3aQueuedBuffRemoval.TargetId);
                    var c3aGolemRows = JsonSerializer.Deserialize<GolemState.Saved[]>(
                        CharacterSaves.ModuleData(c3aCharacter, "Game.Golems")!)!;
                    Assert.DoesNotContain(c3aGolemRows, row => row.Skill == c3aReaddedSource.Id);
                    Assert.Contains(c3aGolemRows, row => row.Effect == c3aStoneDefinition.Id);
                    Assert.DoesNotContain(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(
                        CharacterSaves.ModuleData(c3aCharacter, "Game.SummonedMinions")!)!,
                        row => row.Source == c3aReaddedSource.Id);
                    c3aRemovalSocket = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aGemTarget);
                    Assert.False(c3aRemovalSocket.IsCompleted);
                    var c3aReturnedGem = c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!;
                    var c3aReturnedSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                        gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                    Assert.Equal(new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aReaddedSource.Effect),
                        new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aReturnedSource.Effect));
                    Assert.Equal(c3aReaddedSource.Id, c3aReturnedSource.Id);
                    Assert.Equal(c3aGemType.ExperienceFor(3), c3aReturnedGem.GemExperience);
                    Assert.True(global::Game.SupportedSkill(c3aSelf, c3aReturnedSource.Id).RequirementsMet);
                    Assert.DoesNotContain(c3aRemovalOldId, c3aSelf.Golems.All.Keys);
                    Assert.Null(c3aSelf.GemSourceLifetimes.Current(
                        new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aReaddedSource.Effect)));
                }
                finally
                {
                    c3aRemovalGate.Release();
                }
                Assert.Equal(0u, await c3aStaleFresh!);
                Assert.Equal(0u, await c3aStaleReplacement!);
                await c3aRemovalUnsocket!;
                await c3aRemovalSocket!;
                Assert.Null(c3aSelf.GemSourceLifetimes.Current(
                    new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aReaddedSource.Effect)));
                var c3aRemovalWire = new List<byte[]>();
                var c3aPreviousOutgoing = Program.Outgoing;
                Program.Outgoing = (ciphers, bytes) =>
                {
                    if (ReferenceEquals(ciphers, wire.Ciphers)) lock (c3aRemovalWire) c3aRemovalWire.Add(bytes.ToArray());
                    c3aPreviousOutgoing?.Invoke(ciphers, bytes);
                };
                try { await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, c3aSelf); }
                finally { Program.Outgoing = c3aPreviousOutgoing; }
                Assert.Contains(c3aRemovalWire, packet => packet.SequenceEqual(InstanceEntities.RemoveBuff(
                    Program.PlayerId, c3aFlameBuff.Instance)));
                Assert.DoesNotContain(c3aSelf.Minions.OutputQueue,
                    output => output.Packet.AsSpan().SequenceEqual(InstanceEntities.RemoveBuff(Program.PlayerId, c3aFlameBuff.Instance)));
                c3aSelf.Monsters.Remove(c3aEnemyId);
                c3aRestored = new ClientState(
                    new(1, "atomic-gem-source-restored", 1000, 1000, 100, 100, null, []), 9, 1)
                {
                    Character = c3aCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                    PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = c3aUpdates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    c3aRestored.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                c3aRestored.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 4;
                global::Game.EquipmentStatSources(c3aRestored, c3aInventory.EquipmentSnapshot());
                c3aRestored.Pools = global::Game.PoolsFor(c3aRestored, c3aRestored.Pools.Level);
                c3aRestored.PlayerLife = c3aRestored.Pools.Life;
                c3aRestored.PlayerMana = c3aRestored.Pools.Mana;
                await global::Game.GolemsTick("test", wire.Stream, wire.Ciphers, c3aRestored, c3aSchedule.At);
                await global::Game.SummonsTick("test", wire.Stream, wire.Ciphers, c3aRestored, c3aSchedule.At);
                Assert.Equal(c3aStoneDefinition.Id, Assert.Single(c3aRestored.Golems.All).Value.Definition.Id);
                Assert.Empty(c3aRestored.Summons.Fighters);
                var c3aPostRemovalSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                uint c3aPostRemovalId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aPostRemovalSource.Id, c3aDefinition, (100, 100), arrival: true);
                Assert.NotEqual(0u, c3aPostRemovalId);
                var c3aPostRemoval = c3aSelf.Golems.All[c3aPostRemovalId];

                // Same-effect physical replacement: a real second level-3 Flame gem takes the identical host/socket,
                // so effect, identity and skill id all return unchanged. The old token and actor must still retire.
                var c3aBeforeSwapSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                var c3aBeforeSwapToken = c3aSelf.GemSourceLifetimes.Current(
                    new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aBeforeSwapSource.Effect));
                Assert.NotNull(c3aBeforeSwapToken);
                var c3aPhysicalGem = c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!;
                Assert.Equal(3, global::Game.ItemsFor(c3aSelf).GemLevel(c3aPhysicalGem));
                c3aInventory.Add(PlayerInventories.Cursor, 0, 0, c3aPhysicalGem with { });
                var c3aSwapGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aSwapGate.Wait(0));
                Task<uint>? c3aStaleSwapFresh = null;
                Task<uint>? c3aStaleSwapReplacement = null;
                Task? c3aSameEffectSwap = null;
                try
                {
                    c3aStaleSwapFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aBeforeSwapSource.Id, c3aDefinition, (100, 100), arrival: true);
                    c3aStaleSwapReplacement = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aBeforeSwapSource.Id, c3aDefinition, (100, 100), arrival: true, replacingOld: c3aPostRemoval);
                    Assert.False(c3aStaleSwapFresh.IsCompleted);
                    Assert.False(c3aStaleSwapReplacement.IsCompleted);
                    c3aSameEffectSwap = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aGemTarget);
                    Assert.False(c3aSameEffectSwap.IsCompleted);
                    Assert.DoesNotContain(c3aPostRemovalId, c3aSelf.Golems.All.Keys);
                    Assert.DoesNotContain(c3aPostRemovalId, c3aSelf.Monsters.Keys);
                    Assert.Null(c3aSelf.GemSourceLifetimes.Current(
                        new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aBeforeSwapSource.Effect)));
                    var c3aSwappedGem = c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!;
                    var c3aSwappedSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                        gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                    Assert.Equal(c3aPhysicalGem.TypeHash, c3aSwappedGem.TypeHash);
                    Assert.Equal(c3aPhysicalGem.GemExperience, c3aSwappedGem.GemExperience);
                    Assert.Equal(c3aPhysicalGem.GemVariant, c3aSwappedGem.GemVariant);
                    Assert.Equal(c3aPhysicalGem.Quality, c3aSwappedGem.Quality);
                    Assert.Equal(new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aBeforeSwapSource.Effect),
                        new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aSwappedSource.Effect));
                    Assert.Equal(c3aBeforeSwapSource.Id, c3aSwappedSource.Id);
                    Assert.True(global::Game.SupportedSkill(c3aSelf, c3aSwappedSource.Id).RequirementsMet);
                }
                finally
                {
                    c3aSwapGate.Release();
                }
                Assert.Equal(0u, await c3aStaleSwapFresh!);
                Assert.Equal(0u, await c3aStaleSwapReplacement!);
                await c3aSameEffectSwap!;
                Assert.Null(c3aSelf.GemSourceLifetimes.Current(
                    new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aBeforeSwapSource.Effect)));
                var c3aPostSwapSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                uint c3aPostSwapId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aPostSwapSource.Id, c3aDefinition, (100, 100), arrival: true);
                Assert.NotEqual(0u, c3aPostSwapId);
                var c3aPostSwapToken = c3aSelf.GemSourceLifetimes.Current(
                    new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aBeforeSwapSource.Effect));
                Assert.NotNull(c3aPostSwapToken);
                Assert.NotSame(c3aBeforeSwapToken, c3aPostSwapToken);

                // Positive source-lifetime controls: failed physical handlers, an unchanged skill publication,
                // normal experience and level-up retain the same token. A successful physical removal still retires it.
                var c3aPositiveSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                var c3aPositiveSourceKey = new GemSkillIds.Gem(c3aWeaponSlot.Id, 0, c3aPositiveSource.Effect);
                var c3aLiveToken = c3aSelf.GemSourceLifetimes.Current(c3aPositiveSourceKey);
                Assert.NotNull(c3aLiveToken);
                var c3aSwapHeld = Assert.Single(c3aInventory.Items(PlayerInventories.Cursor));
                Assert.NotNull(c3aInventory.Remove(PlayerInventories.Cursor, c3aSwapHeld.Id));
                await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, c3aSelf, c3aGemTarget);
                Assert.Same(c3aLiveToken, c3aSelf.GemSourceLifetimes.Current(c3aPositiveSourceKey));
                await global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aGemTarget with { Socket = 7 });
                Assert.Same(c3aLiveToken, c3aSelf.GemSourceLifetimes.Current(c3aPositiveSourceKey));
                await global::Game.SendSkills("test", wire.Stream, wire.Ciphers, c3aSelf);
                Assert.Same(c3aLiveToken, c3aSelf.GemSourceLifetimes.Current(c3aPositiveSourceKey));
                int c3aPositiveLevel = global::Game.SupportedSkill(c3aSelf, c3aPositiveSource.Id).Level;
                global::Game.CommitGemExperience(c3aSelf, 1);
                Assert.Equal(c3aPositiveLevel, global::Game.SupportedSkill(c3aSelf, c3aPositiveSource.Id).Level);
                Assert.Same(c3aLiveToken, c3aSelf.GemSourceLifetimes.Current(c3aPositiveSourceKey));
                await global::Game.LevelUpGem("test", wire.Stream, wire.Ciphers, c3aSelf, c3aGemTarget);
                Assert.Equal(c3aPositiveLevel + 1, global::Game.SupportedSkill(c3aSelf, c3aPositiveSource.Id).Level);
                Assert.Same(c3aLiveToken, c3aSelf.GemSourceLifetimes.Current(c3aPositiveSourceKey));

                await global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, c3aSelf, c3aGemTarget);
                Assert.Null(c3aSelf.GemSourceLifetimes.Current(c3aPositiveSourceKey));
                var c3aPositiveHeld = Assert.Single(c3aInventory.Items(PlayerInventories.Cursor));
                await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, c3aSelf, c3aGemTarget);
                Assert.Equal(c3aPhysicalGem.TypeHash, c3aPositiveHeld.Item.TypeHash);
                var c3aAfterReadditionSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                uint c3aAfterReadditionId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers,
                    c3aSelf, c3aAfterReadditionSource.Id, c3aDefinition, (100, 100), arrival: true);
                Assert.NotEqual(0u, c3aAfterReadditionId);
                var c3aAfterReadditionToken = c3aSelf.GemSourceLifetimes.Current(c3aPositiveSourceKey);
                Assert.NotNull(c3aAfterReadditionToken);
                Assert.NotSame(c3aLiveToken, c3aAfterReadditionToken);

                // Real Raise Zombie source and actual public handlers. restoredVariety is a labelled fixture that
                // bypasses corpse admission; it does not replace the production SummonFighter source guard.
                var c3aZombieGem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemRaiseZombie")!);
                c3aInventory.Change(PlayerInventories.Weapon1, c3aWeaponSlot.Id, item => item with
                {
                    Sockets = [.. item.Sockets, new(SocketColour.White, c3aZombieGem)],
                });
                c3aSelf.PlayerStats[Program.Stats.Key("number_of_zombies_allowed")!.Value] = 3;
                c3aRecalculate();
                var c3aZombieSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "RaiseZombie");
                var c3aZombieDefinition = global::Game.SummonData.Find("RaiseZombie")!;
                uint c3aZombieId = await global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aZombieSource.Id, c3aZombieDefinition, (100, 100),
                    restoredVariety: c3aZombieDefinition.Variety);
                Assert.NotEqual(0u, c3aZombieId);
                var c3aZombie = c3aSelf.Summons.Fighters[c3aZombieId];
                var c3aZombieTarget = new InstanceItems.SocketGem(
                    PlayerInventories.InventoryId(PlayerInventories.Weapon1), c3aWeaponSlot.Id, 2);
                var c3aZombieGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aZombieGate.Wait(0));
                Task<uint>? c3aStaleZombieFresh = null;
                Task<uint>? c3aStaleZombieReplacement = null;
                Task? c3aZombieUnsocket = null;
                Task? c3aZombieSocket = null;
                try
                {
                    c3aStaleZombieFresh = global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aZombieSource.Id, c3aZombieDefinition, (100, 100),
                        restoredVariety: c3aZombieDefinition.Variety);
                    c3aStaleZombieReplacement = global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aZombieSource.Id, c3aZombieDefinition, (100, 100),
                        restoredVariety: c3aZombieDefinition.Variety, replacingOld: c3aZombie);
                    Assert.False(c3aStaleZombieFresh.IsCompleted);
                    Assert.False(c3aStaleZombieReplacement.IsCompleted);
                    c3aZombieUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aZombieTarget);
                    Assert.False(c3aZombieUnsocket.IsCompleted);
                    Assert.DoesNotContain(c3aZombieId, c3aSelf.Summons.Fighters.Keys);
                    Assert.DoesNotContain(c3aZombieId, c3aSelf.Summons.Definitions.Keys);
                    Assert.DoesNotContain(c3aZombieId, c3aSelf.Summons.Until.Keys);
                    Assert.DoesNotContain(c3aZombieId, c3aSelf.Monsters.Keys);
                    Assert.False(c3aSelf.Minions.Owns(c3aZombieId, c3aZombie.Monster));
                    Assert.DoesNotContain(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(
                        CharacterSaves.ModuleData(c3aCharacter, "Game.SummonedMinions")!)!,
                        row => row.Source == c3aZombieSource.Id);
                    c3aZombieSocket = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aZombieTarget);
                    Assert.False(c3aZombieSocket.IsCompleted);
                    Assert.Equal(c3aZombieGem.TypeHash,
                        c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[2].Gem!.TypeHash);
                    Assert.DoesNotContain(c3aSelf.Summons.Fighters.Values,
                        fighter => fighter.Source == c3aZombieSource.Id);
                }
                finally
                {
                    c3aZombieGate.Release();
                }
                Assert.Equal(0u, await c3aStaleZombieFresh!);
                Assert.Equal(0u, await c3aStaleZombieReplacement!);
                await c3aZombieUnsocket!;
                await c3aZombieSocket!;
                Assert.DoesNotContain(c3aSelf.Summons.Fighters.Values,
                    fighter => fighter.Source == c3aZombieSource.Id);
                var c3aReturnedZombieSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "RaiseZombie");
                c3aZombieRestored = new ClientState(
                    new(1, "atomic-gem-source-zombie-restored", 1000, 1000, 100, 100, null, []), 10, 1)
                {
                    Character = c3aCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                    PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = c3aUpdates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    c3aZombieRestored.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                c3aZombieRestored.PlayerStats[Program.Stats.Key("number_of_zombies_allowed")!.Value] = 3;
                global::Game.EquipmentStatSources(c3aZombieRestored, c3aInventory.EquipmentSnapshot());
                c3aZombieRestored.Pools = global::Game.PoolsFor(c3aZombieRestored, c3aZombieRestored.Pools.Level);
                c3aZombieRestored.PlayerLife = c3aZombieRestored.Pools.Life;
                c3aZombieRestored.PlayerMana = c3aZombieRestored.Pools.Mana;
                await global::Game.SummonsTick("test", wire.Stream, wire.Ciphers, c3aZombieRestored, c3aSchedule.At);
                Assert.DoesNotContain(c3aZombieRestored.Summons.Fighters.Values,
                    fighter => fighter.Definition.Id == "RaiseZombie");
                uint c3aReturnedZombieId = await global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aReturnedZombieSource.Id, c3aZombieDefinition, (100, 100),
                    restoredVariety: c3aZombieDefinition.Variety);
                Assert.NotEqual(0u, c3aReturnedZombieId);
                var c3aReturnedZombie = c3aSelf.Summons.Fighters[c3aReturnedZombieId];
                var c3aZombieLevelKey = new GemSkillIds.Gem(c3aWeaponSlot.Id, 2, c3aReturnedZombieSource.Effect);

                // The real Raise Zombie source stays on the same physical socket lifetime through the public level-up
                // handler. A fresh create captured at level 1 must reject when that source becomes level 2, while a new
                // create from the current source succeeds at the current supported actor level.
                var c3aZombieLevelToken = c3aSelf.GemSourceLifetimes.Current(c3aZombieLevelKey);
                Assert.NotNull(c3aZombieLevelToken);
                var c3aZombieLevelGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aZombieLevelGate.Wait(0));
                Task<uint>? c3aStaleZombieLevelFresh = null;
                Task? c3aZombieLevelUp = null;
                try
                {
                    c3aStaleZombieLevelFresh = global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aReturnedZombieSource.Id, c3aZombieDefinition, (100, 100),
                        restoredVariety: c3aZombieDefinition.Variety);
                    Assert.False(c3aStaleZombieLevelFresh.IsCompleted,
                        "the actual level-1 fresh zombie did not reach the held create gate");
                    c3aZombieLevelUp = global::Game.LevelUpGem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aZombieTarget);
                    Assert.False(c3aZombieLevelUp.IsCompleted,
                        "the actual Raise Zombie level-up did not suspend after its synchronous mutation");
                    var leveledZombieGem = c3aInventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[2].Gem!;
                    Assert.Equal(2, global::Game.ItemsFor(c3aSelf).GemLevel(leveledZombieGem));
                    Assert.Equal(2, global::Game.SupportedSkill(c3aSelf, c3aReturnedZombieSource.Id).Level);
                    Assert.Same(c3aZombieLevelToken,
                        c3aSelf.GemSourceLifetimes.Current(c3aZombieLevelKey));
                }
                finally { c3aZombieLevelGate.Release(); }
                Assert.Equal(0u, await c3aStaleZombieLevelFresh!);
                await c3aZombieLevelUp!;
                Assert.Same(c3aZombieLevelToken,
                    c3aSelf.GemSourceLifetimes.Current(c3aZombieLevelKey));
                var c3aZombieLevelTwoSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "RaiseZombie");
                int c3aZombieLevelTwoActorLevel = global::Game.SupportedSkill(c3aSelf, c3aZombieLevelTwoSource.Id).Stats
                    .GetValueOrDefault(Program.Stats.Key("base_display_minion_actor_level")!.Value);
                uint c3aZombieLevelTwoId = await global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aZombieLevelTwoSource.Id, c3aZombieDefinition, (100, 100),
                    restoredVariety: c3aZombieDefinition.Variety);
                Assert.NotEqual(0u, c3aZombieLevelTwoId);
                var c3aZombieLevelTwo = c3aSelf.Summons.Fighters[c3aZombieLevelTwoId];
                Assert.Same(c3aZombieLevelTwo, c3aSelf.Summons.Fighters[c3aZombieLevelTwoId]);
                Assert.True(c3aSelf.Minions.Owns(c3aZombieLevelTwoId, c3aZombieLevelTwo.Monster));
                Assert.Equal(c3aZombieLevelTwoActorLevel, c3aZombieLevelTwo.Monster.Level);
                Assert.Same(c3aReturnedZombie, c3aSelf.Summons.Fighters[c3aReturnedZombieId]);

                // The real worn Fleshcrafter changes the current player profile on the same Flame source lifetime. The
                // source remains qualified by the fixture's real attributes, but the fresh create captured from the old
                // profile cannot commit; a new create from the current profile can.
                var c3aProviderBody = Assert.Single(c3aInventory.Items(PlayerInventories.BodyArmour));
                var c3aProviderSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                var c3aProviderToken = c3aSelf.GemSourceLifetimes.Current(c3aFlameSourceKey);
                Assert.NotNull(c3aProviderToken);
                Dictionary<uint, int> c3aProviderBefore;
                using (new global::Game.PlayerStatScope(c3aSelf, fresh: true))
                    c3aProviderBefore = new(global::Game.CharacterPlayerStats(c3aSelf));
                var c3aProviderGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aProviderGate.Wait(0));
                Task<uint>? c3aStaleProviderFresh = null;
                Task? c3aProviderPickup = null;
                try
                {
                    c3aStaleProviderFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aProviderSource.Id, c3aDefinition, (100, 100), arrival: true);
                    Assert.False(c3aStaleProviderFresh.IsCompleted,
                        "the actual Fleshcrafter-profile fresh golem did not reach the held create gate");
                    c3aProviderPickup = global::Game.PickUpItem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        new InstanceItems.PickUpItem(PlayerInventories.InventoryId(PlayerInventories.BodyArmour),
                            c3aProviderBody.Id, 3, 0, 0));
                    Assert.False(c3aProviderPickup.IsCompleted,
                        "the actual Fleshcrafter pickup did not suspend after its synchronous profile mutation");
                    Assert.Empty(c3aInventory.Items(PlayerInventories.BodyArmour));
                    Assert.Equal(c3aProviderBody.Id,
                        Assert.Single(c3aInventory.Items(PlayerInventories.Cursor)).Id);
                    var currentProviderSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                        gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                    Assert.Equal(c3aProviderSource.Id, currentProviderSource.Id);
                    Assert.True(global::Game.SupportedSkill(c3aSelf, currentProviderSource.Id).RequirementsMet);
                    Assert.Same(c3aProviderToken,
                        c3aSelf.GemSourceLifetimes.Current(c3aFlameSourceKey));
                    Dictionary<uint, int> c3aProviderAfter;
                    using (new global::Game.PlayerStatScope(c3aSelf, fresh: true))
                        c3aProviderAfter = new(global::Game.CharacterPlayerStats(c3aSelf));
                    Assert.Contains(c3aProviderBefore, pair => !c3aProviderAfter.TryGetValue(pair.Key, out int value)
                        || value != pair.Value);
                }
                finally { c3aProviderGate.Release(); }
                Assert.Equal(0u, await c3aStaleProviderFresh!);
                await c3aProviderPickup!;
                var c3aProviderCurrentSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                uint c3aProviderFreshId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aProviderCurrentSource.Id, c3aDefinition, (100, 100), arrival: true);
                Assert.NotEqual(0u, c3aProviderFreshId);
                var c3aProviderFresh = c3aSelf.Golems.All[c3aProviderFreshId];
                Assert.True(c3aSelf.Minions.Owns(c3aProviderFreshId, c3aProviderFresh.Monster));
                await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, c3aSelf,
                    new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.BodyArmour),
                        0, 0, 0, 3, 0, 0));
                Assert.Equal(c3aProviderBody.Id, Assert.Single(c3aInventory.Items(PlayerInventories.BodyArmour)).Id);
                Assert.True(global::Game.SupportedSkill(c3aSelf, c3aProviderCurrentSource.Id).RequirementsMet);
                Assert.Same(c3aProviderToken,
                    c3aSelf.GemSourceLifetimes.Current(c3aFlameSourceKey));

                // A below-threshold normal experience share changes the stored gem experience but not level, quality,
                // owned skill stats, player stats, identity or lifetime. The prepared fresh golem must remain admissible.
                var c3aPartialSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "SummonFlameGolem");
                var c3aPartialToken = c3aSelf.GemSourceLifetimes.Current(c3aFlameSourceKey);
                Assert.NotNull(c3aPartialToken);
                int c3aPartialBeforeLevel;
                int c3aPartialBeforeQuality;
                Dictionary<uint, int> c3aPartialOwnBefore;
                Dictionary<uint, int> c3aPartialPlayerBefore;
                using (new global::Game.PlayerStatScope(c3aSelf, fresh: true))
                {
                    var partialBefore = global::Game.SupportedSkill(c3aSelf, c3aPartialSource.Id);
                    c3aPartialBeforeLevel = partialBefore.Level;
                    c3aPartialBeforeQuality = partialBefore.Quality;
                    c3aPartialOwnBefore = new(partialBefore.Stats);
                    c3aPartialPlayerBefore = new(global::Game.CharacterPlayerStats(c3aSelf));
                }
                var c3aPartialGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aPartialGate.Wait(0));
                Task<uint>? c3aPartialFresh = null;
                List<byte[]> c3aPartialPackets = [];
                bool c3aPartialLeveled = false;
                try
                {
                    c3aPartialFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, c3aSelf,
                        c3aPartialSource.Id, c3aDefinition, (100, 100), arrival: true);
                    Assert.False(c3aPartialFresh.IsCompleted,
                        "the actual partial-XP fresh golem did not reach the held create gate");
                    (c3aPartialPackets, c3aPartialLeveled) = global::Game.CommitGemExperience(c3aSelf, 10);
                    Assert.False(c3aPartialLeveled);
                    using (new global::Game.PlayerStatScope(c3aSelf, fresh: true))
                    {
                        var partialAfter = global::Game.SupportedSkill(c3aSelf, c3aPartialSource.Id);
                        var partialPlayerAfter = global::Game.CharacterPlayerStats(c3aSelf);
                        Assert.Equal(c3aPartialBeforeLevel, partialAfter.Level);
                        Assert.Equal(c3aPartialBeforeQuality, partialAfter.Quality);
                        Assert.True(c3aPartialOwnBefore.Count == partialAfter.Stats.Count
                            && c3aPartialOwnBefore.All(pair => partialAfter.Stats.TryGetValue(pair.Key, out int value)
                                && value == pair.Value));
                        Assert.True(c3aPartialPlayerBefore.Count == partialPlayerAfter.Count
                            && c3aPartialPlayerBefore.All(pair => partialPlayerAfter.TryGetValue(pair.Key, out int value)
                                && value == pair.Value));
                    }
                }
                finally { c3aPartialGate.Release(); }
                Assert.Single(c3aPartialPackets);
                var c3aPartialFreshId = await c3aPartialFresh!;
                Assert.NotEqual(0u, c3aPartialFreshId);
                var c3aPartialFreshActor = c3aSelf.Golems.All[c3aPartialFreshId];
                Assert.True(c3aSelf.Minions.Owns(c3aPartialFreshId, c3aPartialFreshActor.Monster));
                Assert.Same(c3aPartialToken,
                    c3aSelf.GemSourceLifetimes.Current(c3aFlameSourceKey));

                // Fixture-only client state, real Animate Guardian source and catalog equipment. One Guardian is already
                // live; both pending admissions prepare the same base gear while the shared send gate is held.
                await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, c3aSelf);
                var c3aGuardianGem = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Gems/SkillGemAnimateArmour")!);
                c3aInventory.Change(PlayerInventories.Weapon1, c3aWeaponSlot.Id, item => item with
                {
                    Sockets = [.. item.Sockets, new(SocketColour.White, c3aGuardianGem)],
                });
                c3aRecalculate();
                var c3aGuardianSource = Assert.Single(global::Game.GemSkillEffects(c3aSelf),
                    gem => global::Game.SkillsFor(c3aSelf).EffectId(gem.Effect) == "AnimateGuardian");
                var c3aGuardianDefinition = global::Game.SummonData.Find("AnimateGuardian")!;
                var c3aGuardianBody = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Armours/BodyArmours/BodyStr1")!);
                var c3aGuardianHelmet = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Armours/Helmets/HelmetStr1")!);
                var c3aGuardianBoots = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Armours/Boots/BootsStr1")!);
                var c3aGuardianShield = ItemCatalog.Create(Program.Items.Find(
                    "Metadata/Items/Armours/Shields/ShieldStr1")!);
                uint c3aGuardianBodyGround = c3aSelf.Instance.Ground.Add(
                    new WorldItemObject(100, 100, c3aGuardianBody));
                uint c3aGuardianLiveId = await global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aGuardianSource.Id, c3aGuardianDefinition, (100, 100), c3aGuardianBodyGround);
                Assert.NotEqual(0u, c3aGuardianLiveId);
                Assert.Null(c3aSelf.Instance.Ground.Get(c3aGuardianBodyGround));
                var c3aGuardianLive = Assert.Single(c3aSelf.Summons.Fighters.Values,
                    fighter => fighter.Definition.Kind == "AnimatedGuardian");
                Assert.Equal(c3aGuardianLiveId, c3aGuardianLive.Id);
                Assert.Same(c3aGuardianBody,
                    Assert.Single(c3aSelf.Summons.GuardianEquipment, slot => slot.Key == 1).Value);
                await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, c3aSelf);

                uint c3aGuardianHelmetGround = c3aSelf.Instance.Ground.Add(
                    new WorldItemObject(100, 100, c3aGuardianHelmet));
                uint c3aGuardianBootsGround = c3aSelf.Instance.Ground.Add(
                    new WorldItemObject(100, 100, c3aGuardianBoots));
                var c3aGuardianRacePackets = new List<byte[]>();
                var c3aGuardianRaceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aGuardianRaceGate.Wait(0));
                var c3aGuardianBeforeRaceOutgoing = Program.Outgoing;
                Program.Outgoing = (ciphers, bytes) =>
                {
                    if (ReferenceEquals(ciphers, wire.Ciphers))
                        lock (c3aGuardianRacePackets) c3aGuardianRacePackets.Add(bytes.ToArray());
                    c3aGuardianBeforeRaceOutgoing?.Invoke(ciphers, bytes);
                };
                Task<uint>? c3aGuardianHelmetAdmission = null;
                Task<uint>? c3aGuardianBootsAdmission = null;
                uint c3aGuardianHelmetId = 0;
                uint c3aGuardianBootsId = 0;
                bool c3aGuardianRaceGateHeld = true;
                uint c3aGuardianObjectStart = c3aSelf.NextObjectId;
                try
                {
                    c3aGuardianHelmetAdmission = global::Game.SummonFighter("test", wire.Stream, wire.Ciphers,
                        c3aSelf, c3aGuardianSource.Id, c3aGuardianDefinition, (100, 100), c3aGuardianHelmetGround);
                    c3aGuardianBootsAdmission = global::Game.SummonFighter("test", wire.Stream, wire.Ciphers,
                        c3aSelf, c3aGuardianSource.Id, c3aGuardianDefinition, (100, 100), c3aGuardianBootsGround);
                    Assert.False(c3aGuardianHelmetAdmission.IsCompleted,
                        "the actual helmet admission did not reach the held send gate");
                    Assert.False(c3aGuardianBootsAdmission.IsCompleted,
                        "the actual boots admission did not reach the held send gate");
                    Assert.Equal(c3aGuardianObjectStart + 2, c3aSelf.NextObjectId);
                    c3aGuardianRaceGate.Release();
                    c3aGuardianRaceGateHeld = false;
                    var c3aGuardianRaceResults =
                        await Task.WhenAll(c3aGuardianHelmetAdmission, c3aGuardianBootsAdmission);
                    c3aGuardianHelmetId = c3aGuardianRaceResults[0];
                    c3aGuardianBootsId = c3aGuardianRaceResults[1];
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, c3aSelf);
                }
                finally
                {
                    if (c3aGuardianRaceGateHeld) c3aGuardianRaceGate.Release();
                    if (c3aGuardianHelmetAdmission is not null && c3aGuardianBootsAdmission is not null)
                        await Task.WhenAll(c3aGuardianHelmetAdmission, c3aGuardianBootsAdmission);
                    Program.Outgoing = c3aGuardianBeforeRaceOutgoing;
                }
                Assert.Equal(1, (c3aGuardianHelmetId != 0 ? 1 : 0) + (c3aGuardianBootsId != 0 ? 1 : 0));
                bool c3aHelmetWon = c3aGuardianHelmetId != 0;
                uint c3aGuardianAcceptedId = c3aHelmetWon ? c3aGuardianHelmetId : c3aGuardianBootsId;
                uint c3aGuardianStaleObjectId = c3aHelmetWon ? c3aGuardianObjectStart + 1 : c3aGuardianObjectStart;
                uint c3aGuardianWinningGround = c3aHelmetWon ? c3aGuardianHelmetGround : c3aGuardianBootsGround;
                uint c3aGuardianRetainedGround = c3aHelmetWon ? c3aGuardianBootsGround : c3aGuardianHelmetGround;
                int c3aGuardianWinningSlot = c3aHelmetWon ? 4 : 9;
                Assert.Null(c3aSelf.Instance.Ground.Get(c3aGuardianWinningGround));
                Assert.Same(c3aHelmetWon ? c3aGuardianBoots : c3aGuardianHelmet,
                    c3aSelf.Instance.Ground.Get(c3aGuardianRetainedGround)!.Item);
                Assert.DoesNotContain(c3aGuardianStaleObjectId, c3aSelf.Summons.Fighters.Keys);
                Assert.DoesNotContain(c3aGuardianStaleObjectId, c3aSelf.Monsters.Keys);
                var c3aGuardianCurrent = Assert.Single(c3aSelf.Summons.Fighters.Values,
                    fighter => fighter.Definition.Kind == "AnimatedGuardian");
                Assert.Equal(c3aGuardianAcceptedId, c3aGuardianCurrent.Id);
                Assert.Equal(new byte[] { 1, (byte)c3aGuardianWinningSlot },
                    c3aSelf.Summons.GuardianEquipment.Keys.Order());
                Assert.Same(c3aGuardianBody, c3aSelf.Summons.GuardianEquipment[1]);
                Assert.Same(c3aHelmetWon ? c3aGuardianHelmet : c3aGuardianBoots,
                    c3aSelf.Summons.GuardianEquipment[(byte)c3aGuardianWinningSlot]);
                Assert.Equal(c3aGuardianCurrent.Equipment, c3aSelf.Summons.GuardianEquipment);

                byte[] c3aGuardianWake = InstanceProtocol.EnterWakeZone(c3aGuardianAcceptedId);
                List<byte[]> c3aGuardianPackets;
                lock (c3aGuardianRacePackets) c3aGuardianPackets = [.. c3aGuardianRacePackets];
                int CountSequence(IEnumerable<byte[]> packets, byte[] expected)
                {
                    int count = 0;
                    foreach (var packet in packets)
                        for (int at = 0; at + expected.Length <= packet.Length; at++)
                            if (packet.AsSpan(at, expected.Length).SequenceEqual(expected)) count++;
                    return count;
                }
                int CountCreate(IEnumerable<byte[]> packets, uint id) => packets.Count(packet => packet.Length >= 6
                    && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId
                    && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == id);
                Assert.Equal(1, CountCreate(c3aGuardianPackets, c3aGuardianAcceptedId));
                Assert.Equal(1, CountSequence(c3aGuardianPackets, c3aGuardianWake));
                Assert.Equal(0, CountCreate(c3aGuardianPackets, c3aGuardianStaleObjectId));
                Assert.Equal(0, CountSequence(c3aGuardianPackets,
                    InstanceProtocol.EnterWakeZone(c3aGuardianStaleObjectId)));
                Assert.Equal(1, CountSequence(c3aGuardianPackets,
                    InstanceProtocol.RemoveObject(c3aGuardianWinningGround)));
                Assert.Equal(1, CountSequence(c3aGuardianPackets,
                    InstanceProtocol.RemoveObject(c3aGuardianLiveId)));
                Assert.Equal(0, CountSequence(c3aGuardianPackets,
                    InstanceProtocol.RemoveObject(c3aGuardianRetainedGround)));
                Assert.Equal(0, CountSequence(c3aGuardianPackets,
                    InstanceProtocol.RemoveObject(c3aGuardianStaleObjectId)));

                global::Game.SaveSummons(c3aSelf);
                var c3aGuardianSaved = JsonSerializer.Deserialize<SummonedMinions.Saved[]>(
                    CharacterSaves.ModuleData(c3aCharacter, "Game.SummonedMinions")!)!;
                var c3aGuardianSavedRow = Assert.Single(c3aGuardianSaved,
                    row => row.Effect == "AnimateGuardian");
                Assert.Equal(c3aGuardianSource.Id, c3aGuardianSavedRow.Source);
                Assert.Equal(c3aGuardianDefinition.Variety, c3aGuardianSavedRow.Variety);
                Assert.Equal(new byte[] { 1, (byte)c3aGuardianWinningSlot },
                    c3aGuardianSavedRow.Equipment!.Keys.Order());
                Assert.Equal(c3aGuardianBody.TypeHash, c3aGuardianSavedRow.Equipment[1].TypeHash);
                Assert.Equal(c3aHelmetWon ? c3aGuardianHelmet.TypeHash : c3aGuardianBoots.TypeHash,
                    c3aGuardianSavedRow.Equipment[(byte)c3aGuardianWinningSlot].TypeHash);
                var c3aGuardianSavedGear = JsonSerializer.Deserialize<Dictionary<byte, Item>>(
                    CharacterSaves.ModuleData(c3aCharacter, "Game.AnimatedGuardianEquipment")!)!;
                Assert.Equal(new byte[] { 1, (byte)c3aGuardianWinningSlot }, c3aGuardianSavedGear.Keys.Order());
                Assert.Equal(c3aGuardianBody.TypeHash, c3aGuardianSavedGear[1].TypeHash);
                Assert.Equal(c3aHelmetWon ? c3aGuardianHelmet.TypeHash : c3aGuardianBoots.TypeHash,
                    c3aGuardianSavedGear[(byte)c3aGuardianWinningSlot].TypeHash);

                uint c3aGuardianRetryId = await global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, c3aSelf,
                    c3aGuardianSource.Id, c3aGuardianDefinition, (100, 100), c3aGuardianRetainedGround);
                Assert.NotEqual(0u, c3aGuardianRetryId);
                Assert.Null(c3aSelf.Instance.Ground.Get(c3aGuardianRetainedGround));
                var c3aGuardianRetry = c3aSelf.Summons.Fighters[c3aGuardianRetryId];
                Assert.Equal(new byte[] { 1, 4, 9 }, c3aGuardianRetry.Equipment.Keys.Order());
                Assert.Same(c3aGuardianBody, c3aGuardianRetry.Equipment[1]);
                Assert.Same(c3aGuardianHelmet, c3aGuardianRetry.Equipment[4]);
                Assert.Same(c3aGuardianBoots, c3aGuardianRetry.Equipment[9]);
                Assert.Equal(c3aGuardianRetry.Equipment, c3aSelf.Summons.GuardianEquipment);
                global::Game.SaveSummons(c3aSelf);
                var c3aGuardianRetrySaved = Assert.Single(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(
                    CharacterSaves.ModuleData(c3aCharacter, "Game.SummonedMinions")!)!,
                    row => row.Effect == "AnimateGuardian");
                Assert.Equal(new byte[] { 1, 4, 9 }, c3aGuardianRetrySaved.Equipment!.Keys.Order());
                Assert.Equal(c3aGuardianSource.Id, c3aGuardianRetrySaved.Source);
                Assert.Equal(c3aGuardianDefinition.Variety, c3aGuardianRetrySaved.Variety);
                Assert.Equal(c3aGuardianBody.TypeHash, c3aGuardianRetrySaved.Equipment[1].TypeHash);
                Assert.Equal(c3aGuardianHelmet.TypeHash, c3aGuardianRetrySaved.Equipment[4].TypeHash);
                Assert.Equal(c3aGuardianBoots.TypeHash, c3aGuardianRetrySaved.Equipment[9].TypeHash);
                var c3aGuardianRetrySavedGear = JsonSerializer.Deserialize<Dictionary<byte, Item>>(
                    CharacterSaves.ModuleData(c3aCharacter, "Game.AnimatedGuardianEquipment")!)!;
                Assert.Equal(new byte[] { 1, 4, 9 }, c3aGuardianRetrySavedGear.Keys.Order());
                Assert.Equal(c3aGuardianBody.TypeHash, c3aGuardianRetrySavedGear[1].TypeHash);
                Assert.Equal(c3aGuardianHelmet.TypeHash, c3aGuardianRetrySavedGear[4].TypeHash);
                Assert.Equal(c3aGuardianBoots.TypeHash, c3aGuardianRetrySavedGear[9].TypeHash);
                await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, c3aSelf);

                // The ground-less return carries its Guardian baseline too. A pending gear refresh based on the current
                // three slots cannot overwrite the newer accepted shield slot; a later fresh ground-less refresh can.
                uint c3aGuardianShieldGround = c3aSelf.Instance.Ground.Add(
                    new WorldItemObject(100, 100, c3aGuardianShield));
                Assert.Empty(c3aSelf.Instance.Ground.Within(1000, 1000, 15, _ => true));
                var c3aGuardianShieldGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aGuardianShieldGate.Wait(0));
                Task<uint>? c3aGuardianShieldAdmission = null;
                Task<uint>? c3aGuardianNoGroundAdmission = null;
                uint c3aGuardianShieldId = 0;
                uint c3aGuardianNoGroundId = 0;
                bool c3aGuardianShieldGateHeld = true;
                uint c3aGuardianShieldObjectStart = c3aSelf.NextObjectId;
                try
                {
                    c3aGuardianShieldAdmission = global::Game.SummonFighter("test", wire.Stream, wire.Ciphers,
                        c3aSelf, c3aGuardianSource.Id, c3aGuardianDefinition, (100, 100), c3aGuardianShieldGround);
                    c3aGuardianNoGroundAdmission = global::Game.SummonFighter("test", wire.Stream, wire.Ciphers,
                        c3aSelf, c3aGuardianSource.Id, c3aGuardianDefinition, (1000, 1000));
                    Assert.False(c3aGuardianShieldAdmission.IsCompleted);
                    Assert.False(c3aGuardianNoGroundAdmission.IsCompleted);
                    Assert.Equal(c3aGuardianShieldObjectStart + 2, c3aSelf.NextObjectId);
                    c3aGuardianShieldGate.Release();
                    c3aGuardianShieldGateHeld = false;
                    var c3aGuardianShieldResults =
                        await Task.WhenAll(c3aGuardianShieldAdmission, c3aGuardianNoGroundAdmission);
                    c3aGuardianShieldId = c3aGuardianShieldResults[0];
                    c3aGuardianNoGroundId = c3aGuardianShieldResults[1];
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, c3aSelf);
                }
                finally
                {
                    if (c3aGuardianShieldGateHeld) c3aGuardianShieldGate.Release();
                    if (c3aGuardianShieldAdmission is not null && c3aGuardianNoGroundAdmission is not null)
                        await Task.WhenAll(c3aGuardianShieldAdmission, c3aGuardianNoGroundAdmission);
                }
                Assert.NotEqual(0u, c3aGuardianShieldId);
                Assert.Equal(0u, c3aGuardianNoGroundId);
                Assert.Null(c3aSelf.Instance.Ground.Get(c3aGuardianShieldGround));
                Assert.DoesNotContain(c3aGuardianShieldObjectStart + 1, c3aSelf.Summons.Fighters.Keys);
                var c3aGuardianShieldCurrent = Assert.Single(c3aSelf.Summons.Fighters.Values,
                    fighter => fighter.Definition.Kind == "AnimatedGuardian");
                Assert.Equal(c3aGuardianShieldId, c3aGuardianShieldCurrent.Id);
                Assert.Equal(new byte[] { 1, 3, 4, 9 }, c3aGuardianShieldCurrent.Equipment.Keys.Order());
                Assert.Equal(new byte[] { 1, 3, 4, 9 }, c3aSelf.Summons.GuardianEquipment.Keys.Order());
                Assert.Same(c3aGuardianShield, c3aSelf.Summons.GuardianEquipment[3]);
                global::Game.SaveSummons(c3aSelf);
                Assert.Equal(new byte[] { 1, 3, 4, 9 }, JsonSerializer.Deserialize<Dictionary<byte, Item>>(
                    CharacterSaves.ModuleData(c3aCharacter, "Game.AnimatedGuardianEquipment")!)!.Keys.Order());

                uint c3aGuardianFreshNoGroundId = await global::Game.SummonFighter("test", wire.Stream, wire.Ciphers,
                    c3aSelf, c3aGuardianSource.Id, c3aGuardianDefinition, (1000, 1000));
                Assert.NotEqual(0u, c3aGuardianFreshNoGroundId);
                var c3aGuardianFreshNoGround = c3aSelf.Summons.Fighters[c3aGuardianFreshNoGroundId];
                Assert.Equal(new byte[] { 1, 3, 4, 9 }, c3aGuardianFreshNoGround.Equipment.Keys.Order());
                Assert.Same(c3aGuardianShield, c3aGuardianFreshNoGround.Equipment[3]);
                Assert.Equal(c3aGuardianFreshNoGround.Equipment, c3aSelf.Summons.GuardianEquipment);
                global::Game.SaveSummons(c3aSelf);
                await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, c3aSelf);

                // A pending gear admission prepared from the live Guardian cannot resurrect after the actual hit/death
                // path clears GuardianEquipment in place and SummonsTick removes the actor.
                c3aSelf.PauseHeld = false;
                if (!c3aSelf.Clock.IsRunning) c3aSelf.Clock.Start();
                Assert.False(c3aSelf.Paused);
                uint c3aGuardianDeathShieldGround = c3aSelf.Instance.Ground.Add(
                    new WorldItemObject(100, 100, c3aGuardianShield));
                var c3aGuardianDeathGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(c3aGuardianDeathGate.Wait(0));
                Task<uint>? c3aGuardianDeathAdmission = null;
                Task? c3aGuardianDeathTick = null;
                try
                {
                    c3aGuardianDeathAdmission = global::Game.SummonFighter("test", wire.Stream, wire.Ciphers,
                        c3aSelf, c3aGuardianSource.Id, c3aGuardianDefinition, (100, 100), c3aGuardianDeathShieldGround);
                    Assert.False(c3aGuardianDeathAdmission.IsCompleted,
                        "the actual shield admission did not reach the held send gate");
                    Assert.Same(c3aGuardianFreshNoGround, Assert.Single(c3aSelf.Summons.Fighters.Values,
                        fighter => fighter.Definition.Kind == "AnimatedGuardian"));
                    Assert.Same(c3aGuardianFreshNoGround.Monster,
                        c3aSelf.Monsters[c3aGuardianFreshNoGround.Id]);
                    var c3aGuardianAttacker = new SpawnedMonster("guardian-race-attacker",
                        new(100, 100, 1), 100, 8, 1000);
                    using (global::Game.ProjectileMutation(c3aSelf, advance: false,
                               phase: "guardian race lethal hit"))
                        Assert.True(global::Game.GolemTakeHitLocked(c3aSelf, c3aGuardianFreshNoGround,
                            0x0A3E3A01, c3aGuardianAttacker,
                            new(0x4000, 1, 100,
                                DamagePacket.Of(DamageType.Physical, 1_000_000_000, 1_000_000_000))
                                { Spell = true }, c3aSchedule.At));
                    Assert.True(c3aGuardianFreshNoGround.Monster.Dead);
                    c3aGuardianDeathTick = global::Game.SummonsTick(
                        "test", wire.Stream, wire.Ciphers, c3aSelf, c3aSchedule.At);
                    Assert.Empty(c3aSelf.Summons.GuardianEquipment);
                    Assert.DoesNotContain(c3aSelf.Summons.Fighters.Values,
                        fighter => fighter.Definition.Kind == "AnimatedGuardian");
                }
                finally
                {
                    c3aGuardianDeathGate.Release();
                    if (c3aGuardianDeathTick is not null) await c3aGuardianDeathTick;
                    if (c3aGuardianDeathAdmission is not null) await c3aGuardianDeathAdmission;
                }
                Assert.Equal(0u, await c3aGuardianDeathAdmission!);
                Assert.Same(c3aGuardianShield, c3aSelf.Instance.Ground.Get(c3aGuardianDeathShieldGround)!.Item);
                Assert.Empty(c3aSelf.Summons.GuardianEquipment);
                await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, c3aSelf);
                global::Game.SaveSummons(c3aSelf);
                Assert.Equal("{}", CharacterSaves.ModuleData(c3aCharacter, "Game.AnimatedGuardianEquipment"));
                Assert.DoesNotContain(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(
                    CharacterSaves.ModuleData(c3aCharacter, "Game.SummonedMinions")!)!,
                    row => row.Effect == "AnimateGuardian");

                var c3aGuardianRestored = new ClientState(
                    new(1, "guardian-death-restored", 1000, 1000, 100, 100, null, []), 11, 1)
                {
                    Character = c3aCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                    PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = c3aUpdates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                try
                {
                    global::Game.EquipmentStatSources(c3aGuardianRestored, c3aInventory.EquipmentSnapshot());
                    c3aGuardianRestored.Pools = global::Game.PoolsFor(c3aGuardianRestored,
                        c3aGuardianRestored.Pools.Level);
                    c3aGuardianRestored.PlayerLife = c3aGuardianRestored.Pools.Life;
                    c3aGuardianRestored.PlayerMana = c3aGuardianRestored.Pools.Mana;
                    await global::Game.SummonsTick("test", wire.Stream, wire.Ciphers,
                        c3aGuardianRestored, c3aSchedule.At);
                    Assert.Empty(c3aGuardianRestored.Summons.GuardianEquipment);
                    Assert.DoesNotContain(c3aGuardianRestored.Summons.Fighters.Values,
                        fighter => fighter.Definition.Kind == "AnimatedGuardian");
                }
                finally { c3aGuardianRestored.Closed = true; }
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousC3AContext;
                if (c3aZombieRestored is not null) c3aZombieRestored.Closed = true;
                if (c3aRestored is not null) c3aRestored.Closed = true;
                if (c3a is not null)
                {
                    c3a.Closed = true;
                    PlayerInventories.Forget(c3aCharacter);
                    global::Game.InactiveItems.TryRemove(c3aCharacter, out _);
                }
            }

            await SharedCharacterGolemSourceAba();
            await ReloginOrphanCleanup();
            await GemProgressionSourceLifetime();
            await PassiveQualificationRetirement();
            await PassiveJewelWriterAuthority();
            await ClusterQualificationRetirement();
            await EquipmentSourceAuthority();
            await DependentOwnerRetirement();
            await PartialPlacementBudget();
            await RighteousFireSourceOwnership();
            await RighteousFireSpellMoreProfile();
            await RighteousFireRadiusProfile();
            await RighteousFireRelightPickAndDiagnosticBoundaries();

            async Task GemProgressionSourceLifetime()
            {
                // Real Flame Golem thresholds with an isolated inventory. Virtual requirement attributes are diagnostic
                // setup; every qualification-changing write below is the actual normal-XP/command/LevelUp path.
                const string progressionCharacter = "GemProgressionSourceLifetime";
                var progressionSchedule = new AreaClockSchedule();
                var progressionUpdates = new PlayerUpdatesState(lifetime, progressionSchedule);
                var previousProgressionContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = progressionUpdates;
                ClientState? progression = null;
                try
                {
                    progressionSchedule.At = 50;
                    var self = progression = new ClientState(
                        new(1, "gem-progression-source-lifetime", 1000, 1000, 100, 100, null, []), 12, 1)
                    {
                        Character = progressionCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
                        PlayerUpdatesState = progressionUpdates,
                        Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                        LeagueModule = conn.LeagueModule,
                    };
                    self.Golems.Restored = true;
                    self.Summons.Restored = true;
                    self.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 2;

                    var inventory = global::Game.ItemsOf(self);
                    var baseType = Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!;
                    var gemType = global::Game.ItemsFor(self).Gem(baseType)!;
                    var gemTemplate = ItemCatalog.Create(baseType);
                    int effect = gemType.Variant(gemTemplate.GemVariant).Effect;
                    Item GemAt(int level) => gemTemplate with { GemExperience = gemType.ExperienceFor(level) };
                    (int Str, int Dex, int Int) Requirements(int level) =>
                        global::Game.GemAttributeRequirements(self, GemAt(level), gemType, effect);
                    static bool Increases((int Str, int Dex, int Int) next, (int Str, int Dex, int Int) before) =>
                        next.Str >= before.Str && next.Dex >= before.Dex && next.Int >= before.Int
                        && next != before;
                    int crossing = Enumerable.Range(1, gemType.MaxLevel - 1).FirstOrDefault(level =>
                        Increases(Requirements(level + 1), Requirements(level)));
                    Assert.InRange(crossing, 1, gemType.MaxLevel - 1);
                    var currentRequirements = Requirements(crossing);
                    var nextRequirements = Requirements(crossing + 1);

                    void SetDiagnosticRequirements((int Str, int Dex, int Int) requirements)
                    {
                        self.PlayerStats[Program.Stats.Key("virtual_strength_for_requirements")!.Value] = requirements.Str;
                        self.PlayerStats[Program.Stats.Key("virtual_dexterity_for_requirements")!.Value] = requirements.Dex;
                        self.PlayerStats[Program.Stats.Key("virtual_intelligence_for_requirements")!.Value] = requirements.Int;
                    }
                    void Recalculate()
                    {
                        global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                        self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                        self.PlayerLife = self.Pools.Life;
                        self.PlayerMana = self.Pools.Mana;
                    }

                    SetDiagnosticRequirements(currentRequirements);
                    var wand = ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!);
                    var host = inventory.Add(PlayerInventories.Weapon1, 0, 0, wand with
                    {
                        Sockets = [new(SocketColour.White, GemAt(crossing))],
                    });
                    Recalculate();

                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        var equipment = inventory.EquipmentSnapshot();
                        return global::Game.PlayerSkillSourcesAt(self.Character, equipment,
                            global::Game.InactiveItems.GetValueOrDefault(self.Character), self.LeagueModule!,
                            [], global::Game.WornGrantedSkillsOf(self, equipment));
                    }
                    global::Game.PlayerSkillSource Source() => Assert.Single(
                        Sources(), source => global::Game.SkillsFor(self).EffectId(source.Effect) == "SummonFlameGolem");
                    Item SocketGem() => inventory.Items(PlayerInventories.Weapon1).Single().Item.Sockets[0].Gem!;
                    bool LastSavedSource(ushort source) =>
                        JsonSerializer.Deserialize<GolemState.Saved[]>(
                            CharacterSaves.ModuleData(progressionCharacter, "Game.Golems") ?? "[]")!
                        .Any(row => row.Skill == source);

                    var source = Source();
                    var sourceKey = source.Identity;
                    var definition = global::Game.GolemData.Find("SummonFlameGolem")!;
                    var totalGolems = Program.Stats.Key("virtual_total_number_of_active_golems")!.Value;
                    var fireGolems = Program.Stats.Key("current_number_of_fire_golems")!.Value;
                    var target = new InstanceItems.SocketGem(
                        PlayerInventories.InventoryId(PlayerInventories.Weapon1), host.Id, 0);
                    var replies = new List<string>();
                    var commands = new ChatCommands
                    {
                        Send = (bytes, label) => Program.Send("test", wire.Stream, wire.Ciphers, bytes, label),
                        Reply = text => { replies.Add(text); return Task.CompletedTask; },
                    };
                    self.Commands = ConditionalSourceTestDrivers.Register(commands, "test", wire.Stream, wire.Ciphers, self);

                    uint actorId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        source.Id, definition, (100, 100), arrival: true);
                    Assert.NotEqual(0u, actorId);
                    var actor = self.Golems.All[actorId];
                    var token = self.GemSourceLifetimes.Current(sourceKey);
                    Assert.NotNull(token);
                    Assert.True(LastSavedSource(source.Id));
                    Assert.True(self.Golems.Buffs.ContainsKey(definition.Buff));
                    Assert.Equal(1, self.Golems.OwnerStats[totalGolems]);
                    Assert.Equal(1, self.Golems.OwnerStats[fireGolems]);

                    var normalGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(normalGate.Wait(0));
                    Task<uint> staleNormalFresh = null!;
                    Task normalXp = null!;
                    try
                    {
                        staleNormalFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            source.Id, definition, (100, 100), arrival: true);
                        Assert.False(staleNormalFresh.IsCompleted);
                        int delta = gemType.ExperienceFor(crossing + 1) - gemType.ExperienceFor(crossing);
                        normalXp = global::Game.GainGemExperience("test", wire.Stream, wire.Ciphers, self,
                            checked((uint)delta * 10));
                        Assert.False(normalXp.IsCompleted,
                            "actual normal gem XP did not suspend after its synchronous qualification mutation");
                        Assert.Equal(crossing, global::Game.ItemsFor(self).GemLevel(SocketGem()));
                        Assert.Equal(gemType.ExperienceFor(crossing + 1) - 1, SocketGem().GemExperience);
                        Assert.True(Source().Active);
                        Assert.True(global::Game.SupportedSkill(self, source.Id).RequirementsMet);
                        Assert.Same(actor, self.Golems.All[actorId]);
                        Assert.Same(token, self.GemSourceLifetimes.Current(sourceKey));
                        Assert.True(LastSavedSource(source.Id));
                    }
                    finally { normalGate.Release(); }
                    actorId = await staleNormalFresh;
                    Assert.NotEqual(0u, actorId); // Stored XP without a level change leaves the prepared summon valid.
                    await normalXp;

                    await commands.RunAsync($"/set_stat strength {nextRequirements.Str}");
                    await commands.RunAsync($"/set_stat dexterity {nextRequirements.Dex}");
                    await commands.RunAsync($"/set_stat intelligence {nextRequirements.Int}");
                    Assert.True(Source().Active);
                    Assert.Same(token, self.GemSourceLifetimes.Current(sourceKey));

                    // Reset the isolated fixture to the crossing level, then make a fresh real fixture mutation lease.
                    inventory.Change(PlayerInventories.Weapon1, host.Id, item => item with
                    {
                        Sockets = [item.Sockets[0] with { Gem = PlayerInventories.PreserveGemIdentity(item.Sockets[0].Gem!, GemAt(crossing)) }],
                    });
                    await commands.RunAsync($"/set_stat strength {currentRequirements.Str}");
                    await commands.RunAsync($"/set_stat dexterity {currentRequirements.Dex}");
                    await commands.RunAsync($"/set_stat intelligence {currentRequirements.Int}");
                    var commandSource = Source();
                    uint commandActorId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        commandSource.Id, definition, (100, 100), arrival: true);
                    Assert.NotEqual(0u, commandActorId);
                    var commandActor = self.Golems.All[commandActorId];
                    var commandToken = self.GemSourceLifetimes.Current(sourceKey);
                    Assert.NotNull(commandToken);
                    Assert.True(LastSavedSource(commandSource.Id));

                    var commandGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(commandGate.Wait(0));
                    Task<uint> staleCommandFresh = null!;
                    Task highCommand = null!;
                    try
                    {
                        staleCommandFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            commandSource.Id, definition, (100, 100), arrival: true);
                        Assert.False(staleCommandFresh.IsCompleted);
                        highCommand = commands.RunAsync($"/gem_xp {gemType.ExperienceFor(crossing + 1)}");
                        Assert.False(highCommand.IsCompleted,
                            "fixture experience dispatch did not suspend after its synchronous qualification mutation");
                        Assert.Equal(crossing + 1, global::Game.ItemsFor(self).GemLevel(SocketGem()));
                        var inactive = Assert.Single(Sources(), row => row.Identity == sourceKey);
                        Assert.False(inactive.Active);
                        Assert.DoesNotContain(commandActorId, self.Golems.All.Keys);
                        Assert.False(self.Minions.Owns(commandActorId, commandActor.Monster));
                        Assert.Null(self.GemSourceLifetimes.Current(sourceKey));
                        Assert.DoesNotContain(self.Golems.Buffs.Keys, buff => buff == definition.Buff);
                        Assert.False(self.Golems.OwnerStats.ContainsKey(fireGolems));
                        Assert.False(LastSavedSource(commandSource.Id));
                    }
                    finally { commandGate.Release(); }
                    Assert.Equal(0u, await staleCommandFresh);
                    await highCommand;
                    Assert.Null(self.GemSourceLifetimes.Current(sourceKey));

                    var lowerGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(lowerGate.Wait(0));
                    Task lowerCommand = null!;
                    try
                    {
                        lowerCommand = commands.RunAsync($"/gem_xp {gemType.ExperienceFor(crossing)}");
                        Assert.False(lowerCommand.IsCompleted);
                        Assert.Equal(crossing, global::Game.ItemsFor(self).GemLevel(SocketGem()));
                        Assert.True(Source().Active);
                        Assert.True(global::Game.SupportedSkill(self, Source().Id).RequirementsMet);
                        Assert.Null(self.GemSourceLifetimes.Current(sourceKey));
                        Assert.Empty(self.Golems.All);
                        Assert.False(LastSavedSource(commandSource.Id));
                    }
                    finally { lowerGate.Release(); }
                    await lowerCommand;
                    Assert.Null(self.GemSourceLifetimes.Current(sourceKey));

                    await commands.RunAsync("/gem_xp near");
                    Assert.Equal(gemType.ExperienceFor(crossing + 1) - 1, SocketGem().GemExperience);
                    Assert.Equal(crossing, global::Game.ItemsFor(self).GemLevel(SocketGem()));
                    Assert.True(Source().Active);
                    Assert.Null(self.GemSourceLifetimes.Current(sourceKey));

                    var restoredSource = Source();
                    uint restoredId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        restoredSource.Id, definition, (100, 100), arrival: true);
                    Assert.NotEqual(0u, restoredId);
                    var restoredActor = self.Golems.All[restoredId];
                    var restoredToken = self.GemSourceLifetimes.Current(sourceKey);
                    Assert.NotNull(restoredToken);
                    Assert.NotSame(commandToken, restoredToken);
                    Assert.True(LastSavedSource(restoredSource.Id));

                    int experienceBeforeRefusal = SocketGem().GemExperience;
                    replies.Clear();
                    await global::Game.LevelUpGem("test", wire.Stream, wire.Ciphers, self, target);
                    Assert.Contains(replies, reply => reply.Contains("requires", StringComparison.OrdinalIgnoreCase));
                    Assert.Equal(experienceBeforeRefusal, SocketGem().GemExperience);
                    Assert.Same(restoredToken, self.GemSourceLifetimes.Current(sourceKey));
                    Assert.Same(restoredActor, self.Golems.All[restoredId]);
                    Assert.True(self.Minions.Owns(restoredId, restoredActor.Monster));
                    Assert.True(self.Golems.Buffs.ContainsKey(definition.Buff));
                    Assert.True(LastSavedSource(restoredSource.Id));

                    var restoredLife = restoredActor.Monster.Life;
                    var restoredShield = restoredActor.Shield;
                    var sameLevelGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(sameLevelGate.Wait(0));
                    Task<uint> sameLevelFresh = null!;
                    Task sameLevelCommand = null!;
                    try
                    {
                        sameLevelFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            restoredSource.Id, definition, (100, 100), arrival: true);
                        Assert.False(sameLevelFresh.IsCompleted);
                        int experienceBeforeSameLevel = SocketGem().GemExperience;
                        sameLevelCommand = commands.RunAsync($"/gem_xp {gemType.ExperienceFor(crossing)}");
                        Assert.False(sameLevelCommand.IsCompleted,
                            "actual /gem_xp same-level update did not suspend after its mutation");
                        Assert.Equal(gemType.ExperienceFor(crossing), SocketGem().GemExperience);
                        Assert.True(SocketGem().GemExperience < experienceBeforeSameLevel);
                        Assert.Equal(crossing, global::Game.ItemsFor(self).GemLevel(SocketGem()));
                        Assert.True(Source().Active);
                        Assert.True(global::Game.SupportedSkill(self, restoredSource.Id).RequirementsMet);
                        Assert.Same(restoredToken, self.GemSourceLifetimes.Current(sourceKey));
                        Assert.Same(restoredActor, self.Golems.All[restoredId]);
                        Assert.Equal(restoredLife, restoredActor.Monster.Life);
                        Assert.Equal(restoredShield, restoredActor.Shield);
                        Assert.True(self.Minions.Owns(restoredId, restoredActor.Monster));
                        Assert.True(self.Golems.Buffs.ContainsKey(definition.Buff));
                        Assert.True(LastSavedSource(restoredSource.Id));
                    }
                    finally { sameLevelGate.Release(); }
                    await sameLevelCommand;
                    uint sameLevelFreshId = await sameLevelFresh;
                    Assert.NotEqual(0u, sameLevelFreshId);
                    var sameLevelFreshActor = self.Golems.All[sameLevelFreshId];
                    Assert.True(self.Minions.Owns(sameLevelFreshId, sameLevelFreshActor.Monster));
                    Assert.Same(restoredToken, self.GemSourceLifetimes.Current(sourceKey));
                }
                finally
                {
                    global::Game.AreaUpdateContext.Value = previousProgressionContext;
                    if (progression is not null)
                    {
                        progression.Closed = true;
                        PlayerInventories.Forget(progressionCharacter);
                        global::Game.InactiveItems.TryRemove(progressionCharacter, out _);
                    }
                }
            }

            async Task PassiveQualificationRetirement()
            {
                // The measured ordinary Witch path: 57226 is reachable from the implicit start and 21678 is its leaf.
                // Real catalogued gems and equipment cross the Clarity 4 / Flame Golem 1 attributes at 42 Intelligence.
                const string retirementCharacter = "PassiveQualificationRetirement";
                const ushort connected = 57226, intelligenceLeaf = 21678;
                var retirementSchedule = new AreaClockSchedule();
                var retirementUpdates = new PlayerUpdatesState(lifetime, retirementSchedule);
                var previousRetirementContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = retirementUpdates;
                ClientState? retirement = null;
                try
                {
                    retirementSchedule.At = 55;
                    PlayerInventories.SetClass(retirementCharacter, 1);
                    CharacterSaves.SetRefundPoints(retirementCharacter, 1);
                    var seed = CharacterSaves.Load(retirementCharacter)!;
                    var self = retirement = new ClientState(
                        new(1, "passive-qualification-retirement", 1000, 1000, 100, 100, null, []), 13, 1)
                    {
                        Character = retirementCharacter, ClassRow = 1, Instance = new(), PlayerSent = true,
                        Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = retirementUpdates,
                        Pools = new(100, 1, 1, 0, 0), LeagueModule = conn.LeagueModule,
                    };
                    self.Passives = [.. seed.Passives ?? []];
                    self.MainAscendancy = seed.MainAscendancy ?? new();
                    self.MasteryEffects = new(seed.MasteryEffects ?? []);
                    self.RefundPoints = seed.RefundPoints ?? 0;
                    self.SavedPassiveProgress = CharacterSaves.PassiveProgressSnapshot.From(seed);
                    self.Golems.Restored = true;
                    self.Summons.Restored = true;
                    self.Walk.Teleport(100, 100, retirementSchedule.At);

                    Item GemAt(string name, int level)
                    {
                        var found = Program.Items.FindGem(name)!.Value;
                        var type = global::Game.ItemsFor(self).Gem(found.Type)!;
                        return ItemCatalog.Create(found.Type) with
                        {
                            GemVariant = found.Variant.Index,
                            GemExperience = type.ExperienceFor(level),
                        };
                    }
                    var clarityGem = GemAt("Clarity", 4);
                    var vitalityGem = GemAt("Vitality", 1);
                    var flameGem = GemAt("Summon Flame Golem", 1);
                    var inventory = global::Game.ItemsOf(self);
                    // One socketed gem's requirements qualify its whole host. Keep the unrelated Vitality source in a
                    // separate real item so it can remain active when Clarity and Flame Golem lose the ten Intelligence.
                    inventory.Add(PlayerInventories.Weapon1, 0, 0,
                        ItemCatalog.Create(Program.Items.Find(
                            "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with
                        {
                            Sockets =
                            [
                                new ItemSocket(SocketColour.White, clarityGem),
                                new ItemSocket(SocketColour.White, flameGem),
                                new ItemSocket(SocketColour.White, null),
                            ],
                            Links = [2],
                        });
                    inventory.Add(PlayerInventories.BodyArmour, 0, 0,
                        ItemCatalog.Create(Program.Items.Find(
                            "Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
                        {
                            Sockets = [new ItemSocket(SocketColour.White, vitalityGem)],
                            Links = [1],
                        });
                    inventory.Add(5, 0, 0, ItemCatalog.Create(
                        Program.Items.Find("Metadata/Items/Amulets/Amulet3")!));
                    inventory.Add(PlayerInventories.Belt, 0, 0, ItemCatalog.Create(
                        Program.Items.Find("Metadata/Items/Belts/Belt4")!));
                    void Recalculate()
                    {
                        global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                        self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                    }
                    Recalculate();
                    self.PlayerLife = self.Pools.Life;
                    self.PlayerMana = self.Pools.Mana;

                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        lock (self.Monsters) return global::Game.CaptureGemSourceRowsLocked(self);
                    }
                    global::Game.PlayerSkillSource Source(string effect) => Assert.Single(
                        Sources(), source => global::Game.SkillsFor(self).EffectId(source.Effect) == effect);
                    Dictionary<uint, int> PlayerStats()
                    {
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            return new(global::Game.CharacterPlayerStats(self));
                    }
                    bool Met(string effect)
                    {
                        var source = Source(effect);
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            return global::Game.SupportedSkill(self, source.Id).RequirementsMet;
                    }
                    void AssertSameAura(AuraCatalog.Aura expected, AuraCatalog.Aura? actual)
                    {
                        Assert.NotNull(actual);
                        Assert.Equal(expected.SkillId, actual.SkillId);
                        Assert.Equal(expected.Effect, actual.Effect);
                        Assert.Equal(expected.Buff, actual.Buff);
                        Assert.Equal(expected.SourceBuff, actual.SourceBuff);
                        Assert.Equal(expected.ManaFlat, actual.ManaFlat);
                        Assert.Equal(expected.ManaPercent, actual.ManaPercent);
                        Assert.Equal(expected.Level, actual.Level);
                        Assert.Equal(expected.Quality, actual.Quality);
                    }
                    void AssertRequirements(int intelligence, bool clarity, bool flame)
                    {
                        var snapshot = global::Game.CurrentGemRequirements(self);
                        Assert.Equal(intelligence, snapshot.Intelligence);
                        Assert.True(snapshot.Strength >= 50);
                        Assert.Equal(clarity,
                            global::Game.GemRequirementsUnmetAt(self, clarityGem, snapshot) is null);
                        Assert.Equal(flame,
                            global::Game.GemRequirementsUnmetAt(self, flameGem, snapshot) is null);
                        Assert.Null(global::Game.GemRequirementsUnmetAt(self, vitalityGem, snapshot));
                        Assert.Equal(clarity, Met("Clarity"));
                        Assert.Equal(flame, Met("SummonFlameGolem"));
                        Assert.True(Met("Vitality"));
                    }

                    AssertRequirements(32, clarity: false, flame: false);
                    Assert.Null(await global::Game.ChangePassives("test", wire.Stream, wire.Ciphers, self,
                        [connected, intelligenceLeaf], true, "qualification fixture"));
                    Assert.Equal([connected, intelligenceLeaf], self.Passives);
                    AssertRequirements(42, clarity: true, flame: true);
                    Assert.Empty(self.Auras);
                    Assert.Empty(self.Golems.All);
                    Assert.Null(self.GemSourceLifetimes.Current(Source("Clarity").Identity));
                    Assert.Null(self.GemSourceLifetimes.Current(Source("SummonFlameGolem").Identity));

                    var claritySource = Source("Clarity");
                    var clarityReservation = global::Game.SupportedReservationCost(self, claritySource.Id);
                    Assert.NotNull(clarityReservation);
                    Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                        claritySource.Id, claritySource.Effect, claritySource.Name, clarityReservation.Value,
                        claritySource.Level, claritySource.Quality));
                    var vitalitySource = Source("Vitality");
                    var vitalityReservation = global::Game.SupportedReservationCost(self, vitalitySource.Id);
                    Assert.NotNull(vitalityReservation);
                    Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                        vitalitySource.Id, vitalitySource.Effect, vitalitySource.Name, vitalityReservation.Value,
                        vitalitySource.Level, vitalitySource.Quality));
                    var oldClarity = Assert.Single(self.Auras.Values, aura => aura.EffectId == "Clarity");
                    var oldVitality = Assert.Single(self.Auras.Values, aura => aura.EffectId == "Vitality");
                    var flameSource = Source("SummonFlameGolem");
                    var flameDefinition = global::Game.GolemData.Find("SummonFlameGolem")!;
                    uint oldFlameId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        flameSource.Id, flameDefinition, (100, 100), arrival: true);
                    Assert.NotEqual(0u, oldFlameId);
                    var oldFlame = self.Golems.All[oldFlameId];
                    var oldLease = self.GemSourceLifetimes.Current(flameSource.Identity);
                    Assert.NotNull(oldLease);
                    Assert.True(self.Minions.Owns(oldFlameId, oldFlame.Monster));
                    Assert.True(LastSavedGolem(retirementCharacter, flameSource.Id));
                    Assert.Contains("Clarity", CharacterSaves.Load(retirementCharacter)!.ActiveAuras ?? []);
                    Assert.Contains("Vitality", CharacterSaves.Load(retirementCharacter)!.ActiveAuras ?? []);

                    var statsWithBoth = PlayerStats();
                    var clarityRegeneration = Program.Stats.Key("base_mana_regeneration_rate_per_minute")!.Value;
                    var vitalityRegeneration = Program.Stats.Key("base_life_regeneration_rate_per_minute")!.Value;
                    Assert.True(statsWithBoth.GetValueOrDefault(clarityRegeneration) > 0);
                    int vitalityRegenerationWithBoth = statsWithBoth.GetValueOrDefault(vitalityRegeneration);
                    uint reservedWithBoth = (uint)global::Game.ReservedMana(self);
                    self.PlayerLife = self.Pools.Life / 2;
                    self.PlayerMana = (uint)global::Game.UnreservedMana(self) / 2;
                    uint lifeBeforeRefund = self.PlayerLife;

                    async Task<List<byte[]>> CaptureHeld(
                        Func<Task<string?>> work, Action suspended, string? expectedErrorSubstring = null)
                    {
                        var packets = new List<byte[]>();
                        var previousOutput = Program.Outgoing;
                        var gate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                        Assert.True(gate.Wait(0));
                        Task<string?>? pending = null;
                        bool released = false;
                        Program.Outgoing = (seen, bytes) =>
                        {
                            if (ReferenceEquals(seen, wire.Ciphers)) lock (packets) packets.Add(bytes.ToArray());
                            previousOutput?.Invoke(seen, bytes);
                        };
                        try
                        {
                            pending = work();
                            Assert.False(pending.IsCompleted,
                                "the accepted mutation did not suspend at its first output");
                            suspended();
                            gate.Release();
                            released = true;
                            var error = await pending;
                            if (expectedErrorSubstring is null) Assert.Null(error);
                            else Assert.Contains(expectedErrorSubstring, error ?? string.Empty);
                            return packets;
                        }
                        finally
                        {
                            try
                            {
                                if (!released) gate.Release();
                                if (pending is not null && !pending.IsCompleted) await pending;
                            }
                            finally { Program.Outgoing = previousOutput; }
                        }
                    }

                    var retirementPackets = await CaptureHeld(
                        () => global::Game.ChangePassives("test", wire.Stream, wire.Ciphers, self,
                            [intelligenceLeaf], false, "refund qualifying leaf", refundPoints: true),
                        () =>
                        {
                            Assert.Equal([connected], self.Passives);
                            Assert.Equal((ushort)0, self.RefundPoints);
                            AssertRequirements(32, clarity: false, flame: false);
                            Assert.DoesNotContain(oldClarity.SkillId, self.Auras.Keys);
                            AssertSameAura(oldVitality, self.Auras.GetValueOrDefault(vitalitySource.Id));
                            Assert.DoesNotContain(oldFlameId, self.Golems.All.Keys);
                            Assert.False(self.Minions.Owns(oldFlameId, oldFlame.Monster));
                            Assert.Null(self.GemSourceLifetimes.Current(flameSource.Identity));
                            Assert.True(oldLease.Retired || !self.GemSourceLifetimes.IsCurrent(oldLease));
                            Assert.False(LastSavedGolem(retirementCharacter, flameSource.Id));
                            var saved = CharacterSaves.Load(retirementCharacter)!;
                            Assert.DoesNotContain("Clarity", saved.ActiveAuras ?? []);
                            Assert.Contains("Vitality", saved.ActiveAuras ?? []);
                            Assert.True((uint)global::Game.ReservedMana(self) < reservedWithBoth);
                            Assert.Equal(lifeBeforeRefund, self.PlayerLife);
                            Assert.True(self.PlayerLife <= self.Pools.Life);
                            Assert.True(self.PlayerMana <= (uint)global::Game.UnreservedMana(self));
                            var stats = PlayerStats();
                            Assert.Equal(0, stats.GetValueOrDefault(clarityRegeneration));
                            Assert.Equal(vitalityRegenerationWithBoth,
                                stats.GetValueOrDefault(vitalityRegeneration));
                        });
                    Assert.Contains(retirementPackets, packet =>
                        packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(Program.PlayerId, oldClarity.Buff)) >= 0);
                    if (oldClarity.SourceBuff is ushort sourceBuff)
                        Assert.Contains(retirementPackets, packet =>
                            packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(Program.PlayerId, sourceBuff)) >= 0);

                    var oldFlameRemoval = InstanceProtocol.RemoveObject(oldFlameId);
                    var previousRemovalOutput = Program.Outgoing;
                    var removalPackets = new List<byte[]>();
                    Program.Outgoing = (seen, bytes) =>
                    {
                        if (ReferenceEquals(seen, wire.Ciphers)) lock (removalPackets) removalPackets.Add(bytes.ToArray());
                        previousRemovalOutput?.Invoke(seen, bytes);
                    };
                    try
                    {
                        await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                    }
                    finally { Program.Outgoing = previousRemovalOutput; }
                    Assert.Contains(retirementPackets.Concat(removalPackets),
                        packet => packet.AsSpan().IndexOf(oldFlameRemoval) >= 0);

                    var restoredPackets = await CaptureHeld(
                        () => global::Game.ChangePassives("test", wire.Stream, wire.Ciphers, self,
                            [intelligenceLeaf], true, "restore qualification fixture"),
                        () =>
                        {
                            Assert.Equal([connected, intelligenceLeaf], self.Passives);
                            AssertRequirements(42, clarity: true, flame: true);
                            var onlyRestoredAura = Assert.Single(self.Auras.Values);
                            Assert.Equal("Vitality", onlyRestoredAura.EffectId);
                            Assert.DoesNotContain(self.Auras.Values,
                                aura => aura.EffectId is "Clarity" or "FlameGolem");
                            AssertSameAura(oldVitality, onlyRestoredAura);
                            Assert.Empty(self.Golems.All);
                            Assert.Null(self.GemSourceLifetimes.Current(flameSource.Identity));
                            var restoredSaved = CharacterSaves.Load(retirementCharacter)!;
                            Assert.Equal(["Vitality"], restoredSaved.ActiveAuras ?? []);
                            Assert.False(LastSavedGolem(retirementCharacter, flameSource.Id));
                        });
                    Assert.DoesNotContain(restoredPackets, packet =>
                        packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(Program.PlayerId, oldClarity.Buff)) >= 0);

                    claritySource = Source("Clarity");
                    clarityReservation = global::Game.SupportedReservationCost(self, claritySource.Id);
                    Assert.NotNull(clarityReservation);
                    Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                        claritySource.Id, claritySource.Effect, claritySource.Name, clarityReservation.Value,
                        claritySource.Level, claritySource.Quality));
                    var newClarity = Assert.Single(self.Auras.Values, aura => aura.EffectId == "Clarity");
                    Assert.NotEqual(oldClarity.Buff, newClarity.Buff);
                    flameSource = Source("SummonFlameGolem");
                    uint newFlameId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        flameSource.Id, flameDefinition, (100, 100), arrival: true);
                    Assert.NotEqual(0u, newFlameId);
                    Assert.NotEqual(oldFlameId, newFlameId);
                    var newFlame = self.Golems.All[newFlameId];
                    var newLease = self.GemSourceLifetimes.Current(flameSource.Identity);
                    Assert.NotNull(newLease);
                    Assert.NotSame(oldLease, newLease);
                    Assert.True(self.Minions.Owns(newFlameId, newFlame.Monster));
                    Assert.True(LastSavedGolem(retirementCharacter, flameSource.Id));

                    var noOpClaritySource = Source("Clarity");
                    var noOpVitalitySource = Source("Vitality");
                    var noOpClarity = self.Auras[noOpClaritySource.Id];
                    var noOpVitality = self.Auras[noOpVitalitySource.Id];
                    var noOpFlameSource = Source("SummonFlameGolem");
                    var noOpFlame = self.Golems.All[newFlameId];
                    var noOpLease = self.GemSourceLifetimes.Current(noOpFlameSource.Identity);
                    Assert.NotNull(noOpLease);
                    uint noOpLife = self.PlayerLife, noOpMana = self.PlayerMana;
                    uint noOpFlameLife = noOpFlame.Monster.Life, noOpFlameShield = noOpFlame.Shield;
                    var noOpSaved = CharacterSaves.Load(retirementCharacter)!;
                    var noOpSavedPassives = noOpSaved.Passives?.ToArray() ?? Array.Empty<ushort>();
                    var noOpSavedRefundPoints = noOpSaved.RefundPoints;
                    var noOpSavedActiveAuras = noOpSaved.ActiveAuras?.ToArray() ?? Array.Empty<string>();
                    bool noOpSavedGolem = LastSavedGolem(retirementCharacter, noOpFlameSource.Id);
                    await CaptureHeld(
                        () => global::Game.ChangePassives("test", wire.Stream, wire.Ciphers, self,
                            [intelligenceLeaf], true, "same qualification no-op"),
                        () =>
                        {
                            AssertSameAura(noOpClarity, self.Auras.GetValueOrDefault(noOpClaritySource.Id));
                            AssertSameAura(noOpVitality, self.Auras.GetValueOrDefault(noOpVitalitySource.Id));
                            Assert.Same(noOpFlame, self.Golems.All[newFlameId]);
                            Assert.Same(noOpLease, self.GemSourceLifetimes.Current(noOpFlameSource.Identity));
                            Assert.Equal(noOpLife, self.PlayerLife);
                            Assert.Equal(noOpMana, self.PlayerMana);
                            Assert.Equal(noOpFlameLife, noOpFlame.Monster.Life);
                            Assert.Equal(noOpFlameShield, noOpFlame.Shield);
                            Assert.True(noOpClaritySource.Equals(Source("Clarity")));
                            var saved = CharacterSaves.Load(retirementCharacter)!;
                            Assert.Equal(noOpSavedPassives, saved.Passives ?? []);
                            Assert.Equal(noOpSavedRefundPoints, saved.RefundPoints);
                            Assert.Equal(noOpSavedActiveAuras, saved.ActiveAuras ?? []);
                            Assert.Equal(noOpSavedGolem, LastSavedGolem(retirementCharacter, noOpFlameSource.Id));
                        });
                    AssertSameAura(noOpClarity, self.Auras.GetValueOrDefault(noOpClaritySource.Id));
                    AssertSameAura(noOpVitality, self.Auras.GetValueOrDefault(noOpVitalitySource.Id));
                    Assert.Same(noOpLease, self.GemSourceLifetimes.Current(noOpFlameSource.Identity));
                    var noOpFinalSaved = CharacterSaves.Load(retirementCharacter)!;
                    Assert.Equal(noOpSavedPassives, noOpFinalSaved.Passives ?? []);
                    Assert.Equal(noOpSavedRefundPoints, noOpFinalSaved.RefundPoints);
                    Assert.Equal(noOpSavedActiveAuras, noOpFinalSaved.ActiveAuras ?? []);
                    Assert.Equal(noOpSavedGolem, LastSavedGolem(retirementCharacter, noOpFlameSource.Id));

                    var refusedClarity = self.Auras[noOpClaritySource.Id];
                    var refusedVitality = self.Auras[noOpVitalitySource.Id];
                    var refusedFlame = self.Golems.All[newFlameId];
                    var refusedLease = self.GemSourceLifetimes.Current(noOpFlameSource.Identity);
                    Assert.NotNull(refusedLease);
                    uint refusedLife = self.PlayerLife, refusedMana = self.PlayerMana;
                    uint refusedFlameLife = refusedFlame.Monster.Life, refusedFlameShield = refusedFlame.Shield;
                    var refusedSaved = CharacterSaves.Load(retirementCharacter)!;
                    var refusedSavedPassives = refusedSaved.Passives?.ToArray() ?? Array.Empty<ushort>();
                    var refusedSavedRefundPoints = refusedSaved.RefundPoints;
                    var refusedSavedActiveAuras = refusedSaved.ActiveAuras?.ToArray() ?? Array.Empty<string>();
                    bool refusedSavedGolem = LastSavedGolem(retirementCharacter, noOpFlameSource.Id);
                    await CaptureHeld(
                        () => global::Game.ChangePassives("test", wire.Stream, wire.Ciphers, self,
                            [intelligenceLeaf], false, "refused refund without points", refundPoints: true),
                        () =>
                        {
                            Assert.Equal([connected, intelligenceLeaf], self.Passives);
                            Assert.Equal((ushort)0, self.RefundPoints);
                            AssertSameAura(refusedClarity, self.Auras.GetValueOrDefault(noOpClaritySource.Id));
                            AssertSameAura(refusedVitality, self.Auras.GetValueOrDefault(noOpVitalitySource.Id));
                            Assert.Same(refusedFlame, self.Golems.All[newFlameId]);
                            Assert.Same(refusedLease, self.GemSourceLifetimes.Current(noOpFlameSource.Identity));
                            Assert.Equal(refusedLife, self.PlayerLife);
                            Assert.Equal(refusedMana, self.PlayerMana);
                            Assert.Equal(refusedFlameLife, refusedFlame.Monster.Life);
                            Assert.Equal(refusedFlameShield, refusedFlame.Shield);
                            Assert.Contains("Clarity", CharacterSaves.Load(retirementCharacter)!.ActiveAuras ?? []);
                            Assert.Contains("Vitality", CharacterSaves.Load(retirementCharacter)!.ActiveAuras ?? []);
                            Assert.True(LastSavedGolem(retirementCharacter, noOpFlameSource.Id));
                            var saved = CharacterSaves.Load(retirementCharacter)!;
                            Assert.Equal(refusedSavedPassives, saved.Passives ?? []);
                            Assert.Equal(refusedSavedRefundPoints, saved.RefundPoints);
                            Assert.Equal(refusedSavedActiveAuras, saved.ActiveAuras ?? []);
                            Assert.Equal(refusedSavedGolem, LastSavedGolem(retirementCharacter, noOpFlameSource.Id));
                        }, "not enough refund points");
                    AssertSameAura(refusedClarity, self.Auras.GetValueOrDefault(noOpClaritySource.Id));
                    AssertSameAura(refusedVitality, self.Auras.GetValueOrDefault(noOpVitalitySource.Id));
                    Assert.Same(refusedLease, self.GemSourceLifetimes.Current(noOpFlameSource.Identity));
                    Assert.Same(refusedFlame, self.Golems.All[newFlameId]);
                    var refusedFinalSaved = CharacterSaves.Load(retirementCharacter)!;
                    Assert.Equal(refusedSavedPassives, refusedFinalSaved.Passives ?? []);
                    Assert.Equal(refusedSavedRefundPoints, refusedFinalSaved.RefundPoints);
                    Assert.Equal(refusedSavedActiveAuras, refusedFinalSaved.ActiveAuras ?? []);
                    Assert.Equal(refusedSavedGolem, LastSavedGolem(retirementCharacter, noOpFlameSource.Id));
                }
                finally
                {
                    global::Game.AreaUpdateContext.Value = previousRetirementContext;
                    if (retirement is not null)
                    {
                        retirement.Closed = true;
                        PlayerInventories.Forget(retirementCharacter);
                        global::Game.InactiveItems.TryRemove(retirementCharacter, out _);
                    }
                }

                bool LastSavedGolem(string character, ushort source) =>
                    JsonSerializer.Deserialize<GolemState.Saved[]>(
                        CharacterSaves.ModuleData(character, "Game.Golems") ?? "[]")!
                    .Any(row => row.Skill == source);
            }

            async Task PassiveJewelWriterAuthority()
            {
                // Real Witch ClassRow 1 path from the measured 82-Intelligence point to ordinary jewel cell 1. Clarity 14
                // needs 93 Intelligence, while Flame Golem's own 35-Intelligence requirement remains met. A real
                // Fertile Mind supplies the missing 16..24 Intelligence and therefore qualifies the whole wand host.
                const string character = "PassiveJewelWriterAuthority";
                const byte witchClassRow = 1;
                ushort[] connectedPath = [57264, 37569, 36542, 4397, 31875, 60398, 7388, 36634];
                const byte socketX = 1;
                var schedule = new AreaClockSchedule();
                var updates = new PlayerUpdatesState(lifetime, schedule);
                var previousContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = updates;
                ClientState? retirement = null;
                try
                {
                    schedule.At = 57;
                    PlayerInventories.SetClass(character, witchClassRow);
                    CharacterSaves.SetRefundPoints(character, 2);
                    var seed = CharacterSaves.Load(character)!;
                    var self = retirement = new ClientState(
                        new(1, "passive-jewel-writer-authority", 1000, 1000, 100, 100, null, []), 14, 1)
                    {
                        Character = character, ClassRow = witchClassRow, Instance = new(), PlayerSent = true,
                        Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                        Pools = new(100, 1, 1, 0, 0),
                        LeagueModule = conn.LeagueModule,
                    };
                    self.Passives = [.. seed.Passives ?? []];
                    self.MainAscendancy = seed.MainAscendancy ?? new();
                    self.MasteryEffects = new(seed.MasteryEffects ?? []);
                    self.RefundPoints = seed.RefundPoints ?? 0;
                    self.SavedPassiveProgress = CharacterSaves.PassiveProgressSnapshot.From(seed);
                    self.Golems.Restored = true;
                    self.Summons.Restored = true;
                    self.Walk.Teleport(100, 100, schedule.At);

                    Item GemAt(string name, int level)
                    {
                        var found = Program.Items.FindGem(name)!.Value;
                        var type = global::Game.ItemsFor(self).Gem(found.Type)!;
                        return ItemCatalog.Create(found.Type) with
                        {
                            GemVariant = found.Variant.Index,
                            GemExperience = type.ExperienceFor(level),
                        };
                    }
                    var clarityGem = GemAt("Clarity", 14);
                    var vitalityGem = GemAt("Vitality", 1);
                    var flameGem = GemAt("Summon Flame Golem", 1);
                    var inventory = global::Game.ItemsOf(self);
                    inventory.Add(PlayerInventories.Weapon1, 0, 0,
                        ItemCatalog.Create(Program.Items.Find(
                            "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with
                        {
                            Sockets =
                            [
                                new ItemSocket(SocketColour.White, clarityGem),
                                new ItemSocket(SocketColour.White, flameGem),
                                new ItemSocket(SocketColour.White, null),
                            ],
                            Links = [2],
                        });
                    inventory.Add(PlayerInventories.BodyArmour, 0, 0,
                        ItemCatalog.Create(Program.Items.Find(
                            "Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
                        {
                            Sockets = [new ItemSocket(SocketColour.White, vitalityGem)],
                            Links = [1],
                        });
                    inventory.Add(5, 0, 0, ItemCatalog.Create(
                        Program.Items.Find("Metadata/Items/Amulets/Amulet3")!));
                    inventory.Add(PlayerInventories.Belt, 0, 0, ItemCatalog.Create(
                        Program.Items.Find("Metadata/Items/Belts/Belt4")!));

                    void Recalculate()
                    {
                        global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                        self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                        self.PlayerLife = self.Pools.Life;
                        self.PlayerMana = self.Pools.Mana;
                    }
                    Recalculate();

                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        lock (self.Monsters) return global::Game.CaptureGemSourceRowsLocked(self);
                    }
                    global::Game.PlayerSkillSource Source(string effect) => Assert.Single(
                        Sources(), source => global::Game.SkillsFor(self).EffectId(source.Effect) == effect);
                    Dictionary<uint, int> PlayerStats()
                    {
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            return new(global::Game.CharacterPlayerStats(self));
                    }
                    bool Met(string effect)
                    {
                        var source = Source(effect);
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            return global::Game.SupportedSkill(self, source.Id).RequirementsMet;
                    }
                    void AssertSameAura(AuraCatalog.Aura expected, AuraCatalog.Aura? actual)
                    {
                        Assert.NotNull(actual);
                        Assert.Equal(expected.SkillId, actual.SkillId);
                        Assert.Equal(expected.Effect, actual.Effect);
                        Assert.Equal(expected.Buff, actual.Buff);
                        Assert.Equal(expected.SourceBuff, actual.SourceBuff);
                        Assert.Equal(expected.ManaFlat, actual.ManaFlat);
                        Assert.Equal(expected.ManaPercent, actual.ManaPercent);
                        Assert.Equal(expected.Level, actual.Level);
                        Assert.Equal(expected.Quality, actual.Quality);
                    }
                    void AssertRequirements(int intelligence, bool clarity, bool flame)
                    {
                        var snapshot = global::Game.CurrentGemRequirements(self);
                        Assert.True(snapshot.Strength >= 50);
                        Assert.Equal(14, snapshot.Dexterity);
                        Assert.Equal(intelligence, snapshot.Intelligence);
                        var clarityType = global::Game.ItemsFor(self).Gem(Program.Items.FindGem("Clarity")!.Value.Type)!;
                        var flameType = global::Game.ItemsFor(self).Gem(
                            Program.Items.FindGem("Summon Flame Golem")!.Value.Type)!;
                        Assert.Equal(93, global::Game.GemAttributeRequirements(self, clarityGem, clarityType,
                            clarityType.Variant(clarityGem.GemVariant).Effect).Int);
                        Assert.Equal(35, global::Game.GemAttributeRequirements(self, flameGem, flameType,
                            flameType.Variant(flameGem.GemVariant).Effect).Int);
                        Assert.Equal(54, global::Game.SkillsFor(self).PlayerLevelReq(
                            clarityType.Variant(clarityGem.GemVariant).Effect,
                            global::Game.ItemsFor(self).GemLevel(clarityGem)));
                        Assert.Equal(clarity,
                            global::Game.GemRequirementsUnmetAt(self, clarityGem, snapshot) is null);
                        Assert.Equal(flame,
                            global::Game.GemRequirementsUnmetAt(self, flameGem, snapshot) is null);
                        Assert.Null(global::Game.GemRequirementsUnmetAt(self, vitalityGem, snapshot));
                        Assert.Equal(clarity, Met("Clarity"));
                        Assert.Equal(clarity && flame, Met("SummonFlameGolem"));
                        Assert.True(Met("Vitality"));
                    }
                    bool LastSavedGolem(ushort source) =>
                        JsonSerializer.Deserialize<GolemState.Saved[]>(
                            CharacterSaves.ModuleData(character, "Game.Golems") ?? "[]")!
                        .Any(row => row.Skill == source);

                    Assert.Null(await global::Game.ChangePassives("test", wire.Stream, wire.Ciphers, self,
                        connectedPath, true, "passive jewel writer fixture"));
                    Assert.Equal(connectedPath, self.Passives);
                    AssertRequirements(82, clarity: false, flame: true);

                    var fertilityDefinition = Program.Items.Mods.ById("IntelligenceUniqueJewel11")!;
                    var conversionDefinition = Program.Items.Mods.ById("JewelDexToInt")!;
                    Item FertileMind(int randomSeed)
                    {
                        var item = new UniqueCatalog(Program.Items).Create("Fertile Mind", new Random(randomSeed));
                        Assert.NotNull(item);
                        var intelligence = Assert.Single(item.Mods, mod => mod.Hash == fertilityDefinition.Hash);
                        Assert.InRange(Assert.Single(intelligence.Values), fertilityDefinition.RangeAt(0).Min,
                            fertilityDefinition.RangeAt(0).Max);
                        Assert.Contains(item.Mods, mod => mod.Hash == conversionDefinition.Hash);
                        return item;
                    }
                    int JewelIntelligence(Item item)
                    {
                        var intelligence = Assert.Single(item.Mods, mod => mod.Hash == fertilityDefinition.Hash);
                        return Assert.Single(intelligence.Values);
                    }
                    void AssertCursor(InventoryItem? expected)
                    {
                        var cursor = inventory.Items(PlayerInventories.Cursor);
                        if (expected is null) Assert.Empty(cursor);
                        else Assert.Equal(expected.Id, Assert.Single(cursor).Id);
                    }
                    async Task PutCursorInMain(InventoryItem expected, byte x)
                    {
                        AssertCursor(expected);
                        await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                                x, 0, 0, 3, 0, 0));
                        Assert.Equal(expected.Id, Assert.Single(inventory.Items(PlayerInventories.MainInventory)).Id);
                        AssertCursor(null);
                    }
                    var fertileA = inventory.Add(PlayerInventories.Cursor, 0, 0, FertileMind(504));
                    AssertCursor(fertileA);
                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            socketX, 0, 0, 3, 0, 0));
                    AssertCursor(null);
                    Assert.Equal(fertileA.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels), item => item.X == socketX).Id);
                    var fertileAIntelligence = JewelIntelligence(fertileA.Item);
                    Assert.InRange(fertileAIntelligence, 16, 24);
                    AssertRequirements(82 + fertileAIntelligence, clarity: true, flame: true);
                    Assert.Equal((ushort)2, self.RefundPoints);

                    var vitalitySource = Source("Vitality");
                    var vitalityReservation = global::Game.SupportedReservationCost(self, vitalitySource.Id);
                    Assert.NotNull(vitalityReservation);
                    Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                        vitalitySource.Id, vitalitySource.Effect, vitalitySource.Name, vitalityReservation.Value,
                        vitalitySource.Level, vitalitySource.Quality));
                    var vitality = Assert.Single(self.Auras.Values, aura => aura.EffectId == "Vitality");
                    var flameDefinition = global::Game.GolemData.Find("SummonFlameGolem")!;

                    async Task<(AuraCatalog.Aura Clarity, uint FlameId, GolemState.Golem Flame,
                        GemSourceLifetimes.SourceLifetime Lease, global::Game.PlayerSkillSource FlameSource)> Activate()
                    {
                        Assert.DoesNotContain(self.Auras.Values, aura => aura.EffectId == "Clarity");
                        var claritySource = Source("Clarity");
                        var clarityReservation = global::Game.SupportedReservationCost(self, claritySource.Id);
                        Assert.NotNull(clarityReservation);
                        Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                            claritySource.Id, claritySource.Effect, claritySource.Name, clarityReservation.Value,
                            claritySource.Level, claritySource.Quality));
                        var flameSource = Source("SummonFlameGolem");
                        uint id = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            flameSource.Id, flameDefinition, (100, 100), arrival: true);
                        Assert.NotEqual(0u, id);
                        var flame = self.Golems.All[id];
                        Assert.True(self.Minions.Owns(id, flame.Monster));
                        var lease = self.GemSourceLifetimes.Current(flameSource.Identity);
                        Assert.NotNull(lease);
                        Assert.Contains("Clarity", CharacterSaves.Load(character)!.ActiveAuras ?? []);
                        Assert.Contains("Vitality", CharacterSaves.Load(character)!.ActiveAuras ?? []);
                        Assert.True(LastSavedGolem(flameSource.Id));
                        return (self.Auras[claritySource.Id], id, flame, lease, flameSource);
                    }
                    var (initialClarity, initialFlameId, initialFlame, initialLease, initialFlameSource) =
                        await Activate();
                    var statsWithBoth = PlayerStats();
                    var clarityRegeneration = Program.Stats.Key("base_mana_regeneration_rate_per_minute")!.Value;
                    var vitalityRegeneration = Program.Stats.Key("base_life_regeneration_rate_per_minute")!.Value;
                    Assert.True(statsWithBoth.GetValueOrDefault(clarityRegeneration) > 0);
                    int vitalityRegenerationWithBoth = statsWithBoth.GetValueOrDefault(vitalityRegeneration);
                    uint reservedWithBoth = (uint)global::Game.ReservedMana(self);

                    async Task<List<byte[]>> CaptureHeld(Func<Task> work, Action suspended, string message)
                    {
                        var packets = new List<byte[]>();
                        var previousOutput = Program.Outgoing;
                        var gate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                        Assert.True(gate.Wait(0));
                        Task? pending = null;
                        bool released = false;
                        Program.Outgoing = (seen, bytes) =>
                        {
                            if (ReferenceEquals(seen, wire.Ciphers)) lock (packets) packets.Add(bytes.ToArray());
                            previousOutput?.Invoke(seen, bytes);
                        };
                        try
                        {
                            pending = work();
                            Assert.False(pending.IsCompleted, message);
                            suspended();
                            gate.Release();
                            released = true;
                            await pending;
                            return packets;
                        }
                        finally
                        {
                            try
                            {
                                if (!released) gate.Release();
                                if (pending is not null && !pending.IsCompleted) await pending;
                            }
                            finally { Program.Outgoing = previousOutput; }
                        }
                    }

                    async Task<List<byte[]>> PublishOutcomes()
                    {
                        var packets = new List<byte[]>();
                        var previousOutput = Program.Outgoing;
                        Program.Outgoing = (seen, bytes) =>
                        {
                            if (ReferenceEquals(seen, wire.Ciphers)) lock (packets) packets.Add(bytes.ToArray());
                            previousOutput?.Invoke(seen, bytes);
                        };
                        try
                        {
                            await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                            return packets;
                        }
                        finally { Program.Outgoing = previousOutput; }
                    }

                    void AssertInventoryPair(IReadOnlyList<byte[]> outputs, byte firstRow, byte secondRow,
                        uint firstRemoved, uint firstAdded, uint secondRemoved, uint secondAdded)
                    {
                        uint firstInventory = PlayerInventories.InventoryId(firstRow);
                        uint secondInventory = PlayerInventories.InventoryId(secondRow);
                        var changes = outputs.Where(packet => packet.Length >= 15
                                && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceItems.InventoryChangeId)
                            .Select(packet =>
                            {
                                uint inventory = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(3));
                                uint revision = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(7));
                                uint removedCount = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(11));
                                uint removedFirst = removedCount == 0
                                    ? 0
                                    : BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(15));
                                int addedCountAt = 15 + checked((int)removedCount * 4);
                                uint addedCount = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(addedCountAt));
                                uint addedFirst = addedCount == 0
                                    ? 0
                                    : BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(addedCountAt + 4));
                                return (Inventory: inventory, Revision: revision,
                                    RemovedFirst: removedFirst, AddedFirst: addedFirst);
                            })
                            .Where(change => change.Inventory == firstInventory || change.Inventory == secondInventory)
                            .ToArray();
                        Assert.Collection(changes,
                            first =>
                            {
                                Assert.Equal(firstInventory, first.Inventory);
                                Assert.Equal(firstRemoved, first.RemovedFirst);
                                Assert.Equal(firstAdded, first.AddedFirst);
                            },
                            second =>
                            {
                                Assert.Equal(secondInventory, second.Inventory);
                                Assert.Equal(secondRemoved, second.RemovedFirst);
                                Assert.Equal(secondAdded, second.AddedFirst);
                            });
                    }

                    void AssertRetiredAtGate(AuraCatalog.Aura oldClarity, uint oldFlameId,
                        GolemState.Golem oldFlame, GemSourceLifetimes.SourceLifetime oldLease,
                        global::Game.PlayerSkillSource oldFlameSource, uint lifeBefore, uint manaBefore)
                    {
                        AssertRequirements(82, clarity: false, flame: true);
                        Assert.DoesNotContain(oldClarity.SkillId, self.Auras.Keys);
                        AssertSameAura(vitality, self.Auras.GetValueOrDefault(vitalitySource.Id));
                        Assert.DoesNotContain(oldFlameId, self.Golems.All.Keys);
                        Assert.False(self.Minions.Owns(oldFlameId, oldFlame.Monster));
                        Assert.Null(self.GemSourceLifetimes.Current(oldFlameSource.Identity));
                        Assert.True(oldLease.Retired || !self.GemSourceLifetimes.IsCurrent(oldLease));
                        Assert.False(LastSavedGolem(oldFlameSource.Id));
                        var saved = CharacterSaves.Load(character)!;
                        Assert.DoesNotContain("Clarity", saved.ActiveAuras ?? []);
                        Assert.Contains("Vitality", saved.ActiveAuras ?? []);
                        Assert.True((uint)global::Game.ReservedMana(self) < reservedWithBoth);
                        Assert.Equal(lifeBefore, self.PlayerLife);
                        Assert.Equal(manaBefore, self.PlayerMana);
                        Assert.True(self.PlayerLife <= self.Pools.Life);
                        Assert.True(self.PlayerMana <= (uint)global::Game.UnreservedMana(self));
                        var stats = PlayerStats();
                        Assert.Equal(0, stats.GetValueOrDefault(clarityRegeneration));
                        Assert.Equal(vitalityRegenerationWithBoth, stats.GetValueOrDefault(vitalityRegeneration));
                    }
                    async Task AssertRemovedPackets(IReadOnlyList<byte[]> producer, AuraCatalog.Aura oldClarity,
                        uint oldFlameId)
                    {
                        var all = producer.Concat(await PublishOutcomes()).ToArray();
                        Assert.Contains(all, packet =>
                            packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(Program.PlayerId, oldClarity.Buff)) >= 0);
                        if (oldClarity.SourceBuff is ushort sourceBuff)
                            Assert.Contains(all, packet =>
                                packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(Program.PlayerId, sourceBuff)) >= 0);
                        Assert.Contains(all, packet =>
                            packet.AsSpan().IndexOf(InstanceProtocol.RemoveObject(oldFlameId)) >= 0);
                    }

                    self.PlayerLife = self.Pools.Life / 2;
                    self.PlayerMana = (uint)global::Game.UnreservedMana(self) / 2;
                    uint pickupLife = self.PlayerLife, pickupMana = self.PlayerMana;
                    var pickupPackets = await CaptureHeld(
                        async () => await global::Game.PickUpItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.PickUpItem(
                                PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                                fertileA.Id, 3, 0, 0)),
                        () =>
                        {
                            Assert.Equal(fertileA.Id, Assert.Single(
                                inventory.Items(PlayerInventories.Cursor)).Id);
                            Assert.Empty(inventory.Items(PlayerInventories.PassiveJewels));
                            AssertRetiredAtGate(initialClarity, initialFlameId, initialFlame, initialLease,
                                initialFlameSource, pickupLife, pickupMana);
                        },
                        "the actual passive-jewel pickup did not suspend at its first output");
                    AssertInventoryPair(pickupPackets, PlayerInventories.PassiveJewels,
                        PlayerInventories.Cursor, fertileA.Id, 0, 0, fertileA.Id);
                    await AssertRemovedPackets(pickupPackets, initialClarity, initialFlameId);

                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            socketX, 0, 0, 3, 0, 0));
                    AssertCursor(null);
                    AssertRequirements(82 + fertileAIntelligence, clarity: true, flame: true);
                    Assert.Empty(self.Golems.All);
                    Assert.Null(self.GemSourceLifetimes.Current(Source("SummonFlameGolem").Identity));
                    void AssertSingleVitality() =>
                        AssertSameAura(vitality, Assert.Single(self.Auras.Values));
                    AssertSingleVitality();

                    var heldJewel = inventory.Add(PlayerInventories.Cursor, 0, 0,
                        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Jewels/JewelInt")!));
                    AssertCursor(heldJewel);
                    Assert.Equal((ushort)2, self.RefundPoints);
                    var (heldClarity, heldFlameId, heldFlame, heldLease, heldFlameSource) = await Activate();
                    self.PlayerLife = self.Pools.Life / 2;
                    self.PlayerMana = (uint)global::Game.UnreservedMana(self) / 2;
                    uint heldLife = self.PlayerLife, heldMana = self.PlayerMana;
                    var heldPackets = await CaptureHeld(
                        async () => await global::Game.HeldOntoItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.ItemAction(
                                PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                                fertileA.Id, 0, 3, 0), "passive jewel held placement"),
                        () =>
                        {
                            Assert.Equal(heldJewel.Id, Assert.Single(
                                inventory.Items(PlayerInventories.PassiveJewels)).Id);
                            Assert.Equal(fertileA.Id, Assert.Single(
                                inventory.Items(PlayerInventories.Cursor)).Id);
                            AssertRetiredAtGate(heldClarity, heldFlameId, heldFlame, heldLease,
                                heldFlameSource, heldLife, heldMana);
                        },
                        "the actual passive-jewel held placement did not suspend at its first output");
                    AssertInventoryPair(heldPackets, PlayerInventories.PassiveJewels,
                        PlayerInventories.Cursor, fertileA.Id, heldJewel.Id, heldJewel.Id, fertileA.Id);
                    await AssertRemovedPackets(heldPackets, heldClarity, heldFlameId);

                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            socketX, 0, 0, 3, 0, 0));
                    AssertCursor(heldJewel);
                    AssertRequirements(82 + fertileAIntelligence, clarity: true, flame: true);
                    AssertSingleVitality();
                    Assert.Empty(self.Golems.All);
                    Assert.Null(self.GemSourceLifetimes.Current(Source("SummonFlameGolem").Identity));

                    var (placeClarity, placeFlameId, placeFlame, placeLease, placeFlameSource) = await Activate();
                    self.PlayerLife = self.Pools.Life / 2;
                    self.PlayerMana = (uint)global::Game.UnreservedMana(self) / 2;
                    uint placeLife = self.PlayerLife, placeMana = self.PlayerMana;
                    var placePackets = await CaptureHeld(
                        async () => await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.PlaceItem(
                                PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                                socketX, 0, 0, 3, 0, 0)),
                        () =>
                        {
                            Assert.Equal(heldJewel.Id, Assert.Single(
                                inventory.Items(PlayerInventories.PassiveJewels)).Id);
                            AssertCursor(fertileA);
                            AssertRetiredAtGate(placeClarity, placeFlameId, placeFlame, placeLease,
                                placeFlameSource, placeLife, placeMana);
                        },
                        "the actual passive-jewel placement swap did not suspend at its first output");
                    AssertInventoryPair(placePackets, PlayerInventories.PassiveJewels,
                        PlayerInventories.Cursor, fertileA.Id, heldJewel.Id, heldJewel.Id, fertileA.Id);
                    await AssertRemovedPackets(placePackets, placeClarity, placeFlameId);

                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            socketX, 0, 0, 3, 0, 0));
                    AssertCursor(heldJewel);
                    AssertRequirements(82 + fertileAIntelligence, clarity: true, flame: true);
                    AssertSingleVitality();
                    await PutCursorInMain(heldJewel, 0);
                    var (qualifiedClarity, qualifiedFlameId, qualifiedFlame, qualifiedLease,
                        qualifiedFlameSource) = await Activate();
                    var fertileB = inventory.Add(PlayerInventories.Cursor, 0, 0, FertileMind(505));
                    AssertCursor(fertileB);
                    var qualifiedPackets = await CaptureHeld(
                        async () => await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.PlaceItem(
                                PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                                socketX, 0, 0, 3, 0, 0)),
                        () =>
                        {
                            Assert.Equal(fertileB.Id, Assert.Single(
                                inventory.Items(PlayerInventories.PassiveJewels)).Id);
                            AssertCursor(fertileA);
                            AssertRequirements(82 + JewelIntelligence(fertileB.Item),
                                clarity: true, flame: true);
                            AssertSameAura(qualifiedClarity,
                                self.Auras.GetValueOrDefault(qualifiedClarity.SkillId));
                            AssertSameAura(vitality, self.Auras.GetValueOrDefault(vitalitySource.Id));
                            Assert.Same(qualifiedFlame, self.Golems.All[qualifiedFlameId]);
                            Assert.Same(qualifiedLease,
                                self.GemSourceLifetimes.Current(qualifiedFlameSource.Identity));
                            Assert.True(self.Minions.Owns(qualifiedFlameId, qualifiedFlame.Monster));
                            Assert.Contains("Clarity", CharacterSaves.Load(character)!.ActiveAuras ?? []);
                            Assert.Contains("Vitality", CharacterSaves.Load(character)!.ActiveAuras ?? []);
                            Assert.True(LastSavedGolem(qualifiedFlameSource.Id));
                        },
                        "the qualified passive-jewel swap did not suspend at its first output");
                    AssertInventoryPair(qualifiedPackets, PlayerInventories.PassiveJewels,
                        PlayerInventories.Cursor, fertileA.Id, fertileB.Id, fertileB.Id, fertileA.Id);
                    var qualifiedPublished = await PublishOutcomes();
                    Assert.DoesNotContain(qualifiedPackets.Concat(qualifiedPublished), packet =>
                        packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(
                            Program.PlayerId, qualifiedClarity.Buff)) >= 0);
                    Assert.DoesNotContain(qualifiedPackets.Concat(qualifiedPublished), packet =>
                        packet.AsSpan().IndexOf(InstanceProtocol.RemoveObject(qualifiedFlameId)) >= 0);

                    // The cursor already holds fertileA: the public pickup writer must refuse without touching the
                    // active owners, the pane, the durable aura/golem state or the refund point.
                    var refusedSaved = CharacterSaves.Load(character)!;
                    var refusedAuras = refusedSaved.ActiveAuras?.ToArray() ?? [];
                    bool refusedGolem = LastSavedGolem(qualifiedFlameSource.Id);
                    await global::Game.PickUpItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PickUpItem(
                            PlayerInventories.InventoryId(PlayerInventories.PassiveJewels), fertileB.Id, 3, 0, 0));
                    Assert.Equal(fertileB.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels)).Id);
                    AssertCursor(fertileA);
                    Assert.Equal((ushort)2, self.RefundPoints);
                    AssertSameAura(qualifiedClarity, self.Auras.GetValueOrDefault(qualifiedClarity.SkillId));
                    AssertSameAura(vitality, self.Auras.GetValueOrDefault(vitalitySource.Id));
                    Assert.Same(qualifiedFlame, self.Golems.All[qualifiedFlameId]);
                    Assert.Same(qualifiedLease,
                        self.GemSourceLifetimes.Current(qualifiedFlameSource.Identity));
                    var refusedAfter = CharacterSaves.Load(character)!;
                    Assert.Equal(refusedAuras, refusedAfter.ActiveAuras ?? []);
                    Assert.Equal((ushort)2, refusedAfter.RefundPoints);
                    Assert.Equal(refusedGolem, LastSavedGolem(qualifiedFlameSource.Id));
                }
                finally
                {
                    global::Game.AreaUpdateContext.Value = previousContext;
                    if (retirement is not null)
                    {
                        retirement.Closed = true;
                        PlayerInventories.Forget(character);
                        global::Game.InactiveItems.TryRemove(character, out _);
                    }
                }
            }

            async Task ClusterQualificationRetirement()
            {
                const string character = "ClusterQualificationRetirement";
                const byte witchClassRow = 1;
                const ushort clusterCell = 20, clusterNode = 202;
                ushort[] connectedPath =
                [
                    57226, 21678, 32210, 8948, 27659, 37671,
                    27415, 32710, 49605, 22618, 21033, 21984,
                ];
                var schedule = new AreaClockSchedule();
                var updates = new PlayerUpdatesState(lifetime, schedule);
                var previousContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = updates;
                ClientState? isolated = null;
                try
                {
                    schedule.At = 59;
                    PlayerInventories.SetClass(character, witchClassRow);
                    CharacterSaves.SetRefundPoints(character, 1);
                    var seed = CharacterSaves.Load(character)!;
                    var self = isolated = new ClientState(
                        new(1, "cluster-qualification-retirement", 1000, 1000, 100, 100, null, []), 15, 1)
                    {
                        Character = character, ClassRow = witchClassRow, Instance = new(), PlayerSent = true,
                        Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = updates,
                        Pools = new(100, 1, 1, 0, 0), LeagueModule = conn.LeagueModule,
                    };
                    self.Passives = [.. seed.Passives ?? []];
                    self.ClusterPassives = [.. seed.ClusterPassives ?? []];
                    self.MainAscendancy = seed.MainAscendancy ?? new();
                    self.MasteryEffects = new(seed.MasteryEffects ?? []);
                    self.RefundPoints = seed.RefundPoints ?? 0;
                    self.SavedPassiveProgress = CharacterSaves.PassiveProgressSnapshot.From(seed);
                    self.Golems.Restored = true;
                    self.Summons.Restored = true;
                    self.Walk.Teleport(100, 100, schedule.At);

                    Item GemAt(string name, int level)
                    {
                        var found = Program.Items.FindGem(name)!.Value;
                        var type = global::Game.ItemsFor(self).Gem(found.Type)!;
                        return ItemCatalog.Create(found.Type) with
                        {
                            GemVariant = found.Variant.Index,
                            GemExperience = type.ExperienceFor(level),
                        };
                    }
                    var clarityGem = GemAt("Clarity", 16);
                    var vitalityGem = GemAt("Vitality", 1);
                    var flameGem = GemAt("Summon Flame Golem", 1);
                    var inventory = global::Game.ItemsOf(self);
                    inventory.Add(PlayerInventories.Weapon1, 0, 0,
                        ItemCatalog.Create(Program.Items.Find(
                            "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with
                        {
                            Sockets =
                            [
                                new ItemSocket(SocketColour.White, clarityGem),
                                new ItemSocket(SocketColour.White, flameGem),
                                new ItemSocket(SocketColour.White, null),
                            ],
                            Links = [2],
                        });
                    inventory.Add(PlayerInventories.BodyArmour, 0, 0,
                        ItemCatalog.Create(Program.Items.Find(
                            "Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
                        {
                            Sockets = [new ItemSocket(SocketColour.White, vitalityGem)],
                            Links = [1],
                        });
                    var amberType = Program.Items.Find("Metadata/Items/Amulets/Amulet3")!;
                    var heavyType = Program.Items.Find("Metadata/Items/Belts/Belt4")!;
                    Assert.Equal("Amber Amulet", amberType.Name);
                    Assert.Equal("Heavy Belt", heavyType.Name);
                    inventory.Add(5, 0, 0, ItemCatalog.Create(amberType));
                    inventory.Add(PlayerInventories.Belt, 0, 0, ItemCatalog.Create(heavyType));

                    void Recalculate()
                    {
                        global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                        self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                        self.PlayerLife = self.Pools.Life;
                        self.PlayerMana = self.Pools.Mana;
                    }
                    Recalculate();

                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        lock (self.Monsters) return global::Game.CaptureGemSourceRowsLocked(self);
                    }
                    global::Game.PlayerSkillSource Source(string effect) => Assert.Single(
                        Sources(), source => global::Game.SkillsFor(self).EffectId(source.Effect) == effect);
                    Dictionary<uint, int> PlayerStats()
                    {
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            return new(global::Game.CharacterPlayerStats(self));
                    }
                    bool Met(string effect)
                    {
                        var source = Source(effect);
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            return global::Game.SupportedSkill(self, source.Id).RequirementsMet;
                    }
                    void AssertAuraValues(AuraCatalog.Aura expected, AuraCatalog.Aura? actual)
                    {
                        Assert.NotNull(actual);
                        Assert.Equal(expected.SkillId, actual.SkillId);
                        Assert.Equal(expected.Effect, actual.Effect);
                        Assert.Equal(expected.EffectId, actual.EffectId);
                        Assert.Equal(expected.Name, actual.Name);
                        Assert.Equal(expected.Buff, actual.Buff);
                        Assert.Equal(expected.BuffRow, actual.BuffRow);
                        Assert.Equal(expected.SourceBuff, actual.SourceBuff);
                        Assert.Equal(expected.ManaFlat, actual.ManaFlat);
                        Assert.Equal(expected.ManaPercent, actual.ManaPercent);
                        Assert.Equal(expected.LifeFlat, actual.LifeFlat);
                        Assert.Equal(expected.LifePercent, actual.LifePercent);
                        Assert.Equal(expected.Level, actual.Level);
                        Assert.Equal(expected.Quality, actual.Quality);
                        Assert.Equal(expected.ReservesLife, actual.ReservesLife);
                        Assert.Equal(expected.AffectsSelf, actual.AffectsSelf);
                        Assert.Equal(expected.SupportEffectPct, actual.SupportEffectPct);
                        Assert.Equal(expected.ReservationEfficiencyPct, actual.ReservationEfficiencyPct);
                    }
                    void AssertRequirements(int intelligence, bool hostQualified)
                    {
                        var snapshot = global::Game.CurrentGemRequirements(self);
                        Assert.Equal(102, intelligence - (hostQualified ? 3 : 0));
                        Assert.Equal(intelligence, snapshot.Intelligence);
                        Assert.Equal(24, snapshot.Dexterity);
                        Assert.True(snapshot.Strength >= 50);
                        var clarityType = global::Game.ItemsFor(self).Gem(
                            Program.Items.FindGem("Clarity")!.Value.Type)!;
                        var flameType = global::Game.ItemsFor(self).Gem(
                            Program.Items.FindGem("Summon Flame Golem")!.Value.Type)!;
                        Assert.Equal(103, global::Game.GemAttributeRequirements(self, clarityGem, clarityType,
                            clarityType.Variant(clarityGem.GemVariant).Effect).Int);
                        Assert.Equal(35, global::Game.GemAttributeRequirements(self, flameGem, flameType,
                            flameType.Variant(flameGem.GemVariant).Effect).Int);
                        Assert.Equal(60, global::Game.SkillsFor(self).PlayerLevelReq(
                            clarityType.Variant(clarityGem.GemVariant).Effect,
                            global::Game.ItemsFor(self).GemLevel(clarityGem)));
                        Assert.Equal(hostQualified,
                            global::Game.GemRequirementsUnmetAt(self, clarityGem, snapshot) is null);
                        Assert.Null(global::Game.GemRequirementsUnmetAt(self, flameGem, snapshot));
                        Assert.Null(global::Game.GemRequirementsUnmetAt(self, vitalityGem, snapshot));
                        Assert.Equal(hostQualified, Met("Clarity"));
                        Assert.Equal(hostQualified, Met("SummonFlameGolem"));
                        Assert.True(Met("Vitality"));
                    }
                    void AssertCursor(InventoryItem? expected)
                    {
                        var cursor = inventory.Items(PlayerInventories.Cursor);
                        if (expected is null) Assert.Empty(cursor);
                        else Assert.Equal(expected.Id, Assert.Single(cursor).Id);
                    }
                    async Task PutCursorInMain(InventoryItem expected, byte x)
                    {
                        AssertCursor(expected);
                        await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.PlaceItem(
                                PlayerInventories.InventoryId(PlayerInventories.MainInventory), x, 0, 0, 3, 0, 0));
                        Assert.Equal(expected.Id, Assert.Single(
                            inventory.Items(PlayerInventories.MainInventory), item => item.X == x).Id);
                        AssertCursor(null);
                    }
                    bool LastSavedGolem(ushort source) =>
                        JsonSerializer.Deserialize<GolemState.Saved[]>(
                            CharacterSaves.ModuleData(character, "Game.Golems") ?? "[]")!
                        .Any(row => row.Skill == source);
                    void SetRefundPoints(ushort points)
                    {
                        self.RefundPoints = points;
                        CharacterSaves.SetRefundPoints(character, points);
                    }

                    Assert.Null(await global::Game.ChangePassives("test", wire.Stream, wire.Ciphers, self,
                        connectedPath, true, "cluster qualification fixture"));
                    Assert.Equal(connectedPath, self.Passives);
                    AssertRequirements(102, hostQualified: false);
                    Assert.Equal((ushort)1, self.RefundPoints);

                    var cluster = ConditionalSourceTestDrivers.CreateClusterJewel(2, 8, 2, null,
                        ["AfflictionJewelSmallPassivesGrantInt_"])!;
                    Assert.Equal(ItemRarity.Magic, cluster.Rarity);
                    Assert.Equal(8, global::Game.ClusterNodeBudget(cluster));
                    Assert.Equal(2, global::Game.ClusterSocketCount(cluster));
                    var explicitIntelligence = Program.Items.Mods.ById("AfflictionJewelSmallPassivesGrantInt_")!;
                    var rolledIntelligence = Assert.Single(cluster.Mods,
                        mod => mod.Hash == explicitIntelligence.Hash);
                    Assert.Equal([3], rolledIntelligence.Values);
                    Assert.Contains(global::Game.ItemsFor(self).Mods.ByHash(explicitIntelligence.Hash)!.Stats,
                        stat => stat.Id == "local_affliction_jewel_small_nodes_grant_int");

                    var clusterHeld = inventory.Add(PlayerInventories.Cursor, 0, 0, cluster);
                    Assert.Equal(clusterHeld.Id, Assert.Single(
                        inventory.Items(PlayerInventories.Cursor)).Id);
                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(
                            PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            clusterCell, 0, 0, 3, 0, 0));
                    AssertCursor(null);
                    Assert.Equal(clusterHeld.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);
                    var layout = ClusterJewelLayout.For(clusterCell, clusterHeld.Item);
                    Assert.NotNull(layout);
                    Assert.Equal(195, layout.Base);
                    Assert.Equal(12, layout.Total);
                    Assert.Equal(ClusterJewelLayout.Kind.Small,
                        layout.ByOffset.GetValueOrDefault(clusterNode - layout.Base));
                    Assert.Empty(self.ClusterPassives);
                    AssertRequirements(102, hostQualified: false);

                    await global::Game.ChangeClusterPassive("test", wire.Stream, wire.Ciphers, self,
                        clusterNode, true);
                    Assert.Equal([clusterNode], self.ClusterPassives);
                    Assert.Equal([clusterNode], CharacterSaves.Load(character)!.ClusterPassives ?? []);
                    AssertRequirements(105, hostQualified: true);

                    var vitalitySource = Source("Vitality");
                    var vitalityReservation = global::Game.SupportedReservationCost(self, vitalitySource.Id);
                    Assert.NotNull(vitalityReservation);
                    Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                        vitalitySource.Id, vitalitySource.Effect, vitalitySource.Name, vitalityReservation.Value,
                        vitalitySource.Level, vitalitySource.Quality));
                    var vitality = Assert.Single(self.Auras.Values, aura => aura.EffectId == "Vitality");
                    var expectedVitality = vitality with { };
                    var flameDefinition = global::Game.GolemData.Find("SummonFlameGolem")!;

                    async Task<(AuraCatalog.Aura Clarity, uint FlameId, GolemState.Golem Flame,
                        GemSourceLifetimes.SourceLifetime Lease, global::Game.PlayerSkillSource FlameSource)> Activate()
                    {
                        Assert.DoesNotContain(self.Auras.Values, aura => aura.EffectId == "Clarity");
                        var claritySource = Source("Clarity");
                        var clarityReservation = global::Game.SupportedReservationCost(self, claritySource.Id);
                        Assert.NotNull(clarityReservation);
                        Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                            claritySource.Id, claritySource.Effect, claritySource.Name, clarityReservation.Value,
                            claritySource.Level, claritySource.Quality));
                        var flameSource = Source("SummonFlameGolem");
                        uint id = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            flameSource.Id, flameDefinition, (100, 100), arrival: true);
                        Assert.NotEqual(0u, id);
                        var flame = self.Golems.All[id];
                        Assert.True(self.Minions.Owns(id, flame.Monster));
                        var lease = self.GemSourceLifetimes.Current(flameSource.Identity);
                        Assert.NotNull(lease);
                        Assert.Contains("Clarity", CharacterSaves.Load(character)!.ActiveAuras ?? []);
                        Assert.Contains("Vitality", CharacterSaves.Load(character)!.ActiveAuras ?? []);
                        Assert.True(LastSavedGolem(flameSource.Id));
                        return (self.Auras[claritySource.Id], id, flame, lease, flameSource);
                    }
                    var (oldClarity, oldFlameId, oldFlame, oldLease, oldFlameSource) = await Activate();
                    var statsWithBoth = PlayerStats();
                    var clarityRegeneration = Program.Stats.Key("base_mana_regeneration_rate_per_minute")!.Value;
                    var vitalityRegeneration = Program.Stats.Key("base_life_regeneration_rate_per_minute")!.Value;
                    Assert.True(statsWithBoth.GetValueOrDefault(clarityRegeneration) > 0);
                    int vitalityRegenerationWithBoth = statsWithBoth.GetValueOrDefault(vitalityRegeneration);
                    uint reservedWithBoth = (uint)global::Game.ReservedMana(self);

                    async Task<List<byte[]>> CaptureHeld(Func<Task> work, Action suspended, string message)
                    {
                        var packets = new List<byte[]>();
                        var previousOutput = Program.Outgoing;
                        var gate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                        Assert.True(gate.Wait(0));
                        Task? pending = null;
                        bool released = false;
                        Program.Outgoing = (seen, bytes) =>
                        {
                            if (ReferenceEquals(seen, wire.Ciphers)) lock (packets) packets.Add(bytes.ToArray());
                            previousOutput?.Invoke(seen, bytes);
                        };
                        try
                        {
                            pending = work();
                            Assert.False(pending.IsCompleted, message);
                            suspended();
                            gate.Release();
                            released = true;
                            await pending;
                            return packets;
                        }
                        finally
                        {
                            try
                            {
                                if (!released) gate.Release();
                                if (pending is not null && !pending.IsCompleted) await pending;
                            }
                            finally { Program.Outgoing = previousOutput; }
                        }
                    }

                    async Task<List<byte[]>> PublishOutcomes()
                    {
                        var packets = new List<byte[]>();
                        var previousOutput = Program.Outgoing;
                        Program.Outgoing = (seen, bytes) =>
                        {
                            if (ReferenceEquals(seen, wire.Ciphers)) lock (packets) packets.Add(bytes.ToArray());
                            previousOutput?.Invoke(seen, bytes);
                        };
                        try
                        {
                            await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                            return packets;
                        }
                        finally { Program.Outgoing = previousOutput; }
                    }

                    async Task<List<byte[]>> Capture(Func<Task> work)
                    {
                        var packets = new List<byte[]>();
                        var previousOutput = Program.Outgoing;
                        Program.Outgoing = (seen, bytes) =>
                        {
                            if (ReferenceEquals(seen, wire.Ciphers)) lock (packets) packets.Add(bytes.ToArray());
                            previousOutput?.Invoke(seen, bytes);
                        };
                        try
                        {
                            await work();
                            return packets;
                        }
                        finally { Program.Outgoing = previousOutput; }
                    }

                    void AssertInventoryPair(IReadOnlyList<byte[]> outputs, byte firstRow, byte secondRow,
                        uint firstRemoved, uint firstAdded, uint secondRemoved, uint secondAdded)
                    {
                        uint firstInventory = PlayerInventories.InventoryId(firstRow);
                        uint secondInventory = PlayerInventories.InventoryId(secondRow);
                        var changes = outputs.Where(packet => packet.Length >= 15
                                && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceItems.InventoryChangeId)
                            .Select(packet =>
                            {
                                uint inventory = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(3));
                                uint removedCount = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(11));
                                uint removedFirst = removedCount == 0
                                    ? 0
                                    : BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(15));
                                int addedCountAt = 15 + checked((int)removedCount * 4);
                                uint addedCount = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(addedCountAt));
                                uint addedFirst = addedCount == 0
                                    ? 0
                                    : BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(addedCountAt + 4));
                                return (Inventory: inventory, RemovedFirst: removedFirst, AddedFirst: addedFirst);
                            })
                            .Where(change => change.Inventory == firstInventory || change.Inventory == secondInventory)
                            .ToArray();
                        Assert.Collection(changes,
                            first =>
                            {
                                Assert.Equal(firstInventory, first.Inventory);
                                Assert.Equal(firstRemoved, first.RemovedFirst);
                                Assert.Equal(firstAdded, first.AddedFirst);
                            },
                            second =>
                            {
                                Assert.Equal(secondInventory, second.Inventory);
                                Assert.Equal(secondRemoved, second.RemovedFirst);
                                Assert.Equal(secondAdded, second.AddedFirst);
                            });
                    }

                    self.PlayerLife = self.Pools.Life / 2;
                    self.PlayerMana = (uint)global::Game.UnreservedMana(self) / 2;
                    uint lifeBeforeRefund = self.PlayerLife, manaBeforeRefund = self.PlayerMana;
                    var retirementPackets = await CaptureHeld(
                        () => global::Game.ChangeClusterPassive("test", wire.Stream, wire.Ciphers, self,
                            clusterNode, false),
                        () =>
                        {
                            Assert.Empty(self.ClusterPassives);
                            AssertRequirements(102, hostQualified: false);
                            Assert.DoesNotContain(oldClarity.SkillId, self.Auras.Keys);
                            AssertAuraValues(expectedVitality, self.Auras.GetValueOrDefault(vitalitySource.Id));
                            Assert.DoesNotContain(oldFlameId, self.Golems.All.Keys);
                            Assert.False(self.Minions.Owns(oldFlameId, oldFlame.Monster));
                            Assert.Null(self.GemSourceLifetimes.Current(oldFlameSource.Identity));
                            Assert.True(oldLease.Retired || !self.GemSourceLifetimes.IsCurrent(oldLease));
                            Assert.False(LastSavedGolem(oldFlameSource.Id));
                            Assert.Empty(self.Summons.Fighters);
                            var saved = CharacterSaves.Load(character)!;
                            Assert.Empty(saved.ClusterPassives ?? []);
                            Assert.DoesNotContain("Clarity", saved.ActiveAuras ?? []);
                            Assert.Contains("Vitality", saved.ActiveAuras ?? []);
                            Assert.True((uint)global::Game.ReservedMana(self) < reservedWithBoth);
                            Assert.Equal(lifeBeforeRefund, self.PlayerLife);
                            Assert.Equal(manaBeforeRefund, self.PlayerMana);
                            Assert.True(self.PlayerLife <= self.Pools.Life);
                            Assert.True(self.PlayerMana <= (uint)global::Game.UnreservedMana(self));
                            var stats = PlayerStats();
                            Assert.Equal(0, stats.GetValueOrDefault(clarityRegeneration));
                            Assert.Equal(vitalityRegenerationWithBoth,
                                stats.GetValueOrDefault(vitalityRegeneration));
                        },
                        "the actual cluster refund did not suspend at its first tree output");
                    var removed = retirementPackets.Concat(await PublishOutcomes()).ToArray();
                    Assert.Contains(removed, packet =>
                        packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(
                            Program.PlayerId, oldClarity.Buff)) >= 0);
                    if (oldClarity.SourceBuff is ushort sourceBuff)
                        Assert.Contains(removed, packet =>
                            packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(
                                Program.PlayerId, sourceBuff)) >= 0);
                    Assert.Contains(removed, packet =>
                        packet.AsSpan().IndexOf(InstanceProtocol.RemoveObject(oldFlameId)) >= 0);

                    await global::Game.ChangeClusterPassive("test", wire.Stream, wire.Ciphers, self,
                        clusterNode, true);
                    Assert.Equal([clusterNode], self.ClusterPassives);
                    AssertRequirements(105, hostQualified: true);
                    Assert.DoesNotContain(self.Auras.Values, aura => aura.EffectId == "Clarity");
                    Assert.Empty(self.Golems.All);
                    Assert.Null(self.GemSourceLifetimes.Current(oldFlameSource.Identity));
                    AssertAuraValues(expectedVitality, Assert.Single(self.Auras.Values));
                    var (freshClarity, freshFlameId, freshFlame, freshLease, freshFlameSource) = await Activate();
                    Assert.NotEqual(oldClarity.Buff, freshClarity.Buff);
                    Assert.NotEqual(oldClarity.SourceBuff, freshClarity.SourceBuff);
                    Assert.NotEqual(oldFlameId, freshFlameId);
                    Assert.NotSame(oldLease, freshLease);

                    var expectedFreshClarity = freshClarity with { };
                    var expectedFreshVitality = vitality with { };
                    uint noOpLife = self.PlayerLife, noOpMana = self.PlayerMana;
                    uint noOpFlameLife = freshFlame.Monster.Life, noOpFlameShield = freshFlame.Shield;
                    var noOpSaved = CharacterSaves.Load(character)!;
                    var noOpSavedClusters = noOpSaved.ClusterPassives?.ToArray() ?? [];
                    var noOpSavedAuras = noOpSaved.ActiveAuras?.ToArray() ?? [];
                    bool noOpSavedGolem = LastSavedGolem(freshFlameSource.Id);
                    await CaptureHeld(
                        () => global::Game.ChangeClusterPassive("test", wire.Stream, wire.Ciphers, self,
                            clusterNode, true),
                        () =>
                        {
                            Assert.Equal([clusterNode], self.ClusterPassives);
                            AssertAuraValues(expectedFreshClarity,
                                self.Auras.GetValueOrDefault(freshClarity.SkillId));
                            AssertAuraValues(expectedFreshVitality,
                                self.Auras.GetValueOrDefault(vitalitySource.Id));
                            Assert.Same(freshFlame, self.Golems.All[freshFlameId]);
                            Assert.Same(freshLease,
                                self.GemSourceLifetimes.Current(freshFlameSource.Identity));
                            Assert.Equal(noOpLife, self.PlayerLife);
                            Assert.Equal(noOpMana, self.PlayerMana);
                            Assert.Equal(noOpFlameLife, freshFlame.Monster.Life);
                            Assert.Equal(noOpFlameShield, freshFlame.Shield);
                            var saved = CharacterSaves.Load(character)!;
                            Assert.Equal(noOpSavedClusters, saved.ClusterPassives ?? []);
                            Assert.Equal(noOpSavedAuras, saved.ActiveAuras ?? []);
                            Assert.Equal(noOpSavedGolem, LastSavedGolem(freshFlameSource.Id));
                        },
                        "the qualified same-node allocation did not suspend at its first tree output");
                    AssertAuraValues(expectedFreshClarity, self.Auras.GetValueOrDefault(freshClarity.SkillId));
                    Assert.Same(freshLease, self.GemSourceLifetimes.Current(freshFlameSource.Identity));

                    await CaptureHeld(
                        () => global::Game.ChangeClusterPassive("test", wire.Stream, wire.Ciphers, self,
                            (ushort)(global::Game.MaxClusterNodeId + 1), true),
                        () =>
                        {
                            Assert.Equal([clusterNode], self.ClusterPassives);
                            AssertAuraValues(expectedFreshClarity,
                                self.Auras.GetValueOrDefault(freshClarity.SkillId));
                            AssertAuraValues(expectedFreshVitality,
                                self.Auras.GetValueOrDefault(vitalitySource.Id));
                            Assert.Same(freshFlame, self.Golems.All[freshFlameId]);
                            Assert.Same(freshLease,
                                self.GemSourceLifetimes.Current(freshFlameSource.Identity));
                            Assert.Equal(noOpLife, self.PlayerLife);
                            Assert.Equal(noOpMana, self.PlayerMana);
                            Assert.Equal(noOpFlameLife, freshFlame.Monster.Life);
                            Assert.Equal(noOpFlameShield, freshFlame.Shield);
                            var saved = CharacterSaves.Load(character)!;
                            Assert.Equal(noOpSavedClusters, saved.ClusterPassives ?? []);
                            Assert.Equal(noOpSavedAuras, saved.ActiveAuras ?? []);
                            Assert.Equal(noOpSavedGolem, LastSavedGolem(freshFlameSource.Id));
                        },
                        "the refused out-of-range cluster node did not suspend at its tree output");
                    Assert.Equal([clusterNode], self.ClusterPassives);
                    AssertAuraValues(expectedFreshClarity, self.Auras.GetValueOrDefault(freshClarity.SkillId));
                    Assert.Same(freshFlame, self.Golems.All[freshFlameId]);
                    Assert.Same(freshLease, self.GemSourceLifetimes.Current(freshFlameSource.Identity));

                    AssertCursor(null);
                    Assert.Equal((ushort)1, self.RefundPoints);
                    await global::Game.PickUpItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PickUpItem(
                            PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            clusterHeld.Id, 3, 0, 0));
                    AssertCursor(null);
                    Assert.Equal(clusterHeld.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);
                    Assert.Equal((ushort)1, self.RefundPoints);

                    var replacementA = inventory.Add(PlayerInventories.Cursor, 0, 0,
                        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Jewels/JewelInt")!));
                    AssertCursor(replacementA);
                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(
                            PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            clusterCell, 0, 0, 3, 0, 0));
                    AssertCursor(replacementA);
                    Assert.Equal(clusterHeld.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);
                    await PutCursorInMain(replacementA, 0);

                    var replacementB = inventory.Add(PlayerInventories.Cursor, 0, 0,
                        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Jewels/JewelInt")!));
                    await global::Game.HeldOntoItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.ItemAction(
                            PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            clusterHeld.Id, 0, 3, 0), "cluster allocation removal refusal");
                    AssertCursor(replacementB);
                    Assert.Equal(clusterHeld.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);
                    await PutCursorInMain(replacementB, 1);
                    Assert.Equal((ushort)1, self.RefundPoints);
                    AssertAuraValues(expectedFreshClarity, self.Auras.GetValueOrDefault(freshClarity.SkillId));
                    Assert.Same(freshFlame, self.Golems.All[freshFlameId]);
                    Assert.Same(freshLease, self.GemSourceLifetimes.Current(freshFlameSource.Identity));
                    Assert.Equal(noOpSavedClusters, CharacterSaves.Load(character)!.ClusterPassives ?? []);
                    Assert.Equal(noOpSavedAuras, CharacterSaves.Load(character)!.ActiveAuras ?? []);
                    Assert.True(LastSavedGolem(freshFlameSource.Id));

                    await global::Game.ChangeClusterPassive("test", wire.Stream, wire.Ciphers, self,
                        clusterNode, false);
                    await PublishOutcomes();
                    Assert.Empty(self.ClusterPassives);
                    AssertRequirements(102, hostQualified: false);
                    Assert.DoesNotContain(self.Auras.Values, aura => aura.EffectId == "Clarity");
                    Assert.Empty(self.Golems.All);
                    AssertAuraValues(expectedVitality, Assert.Single(self.Auras.Values));
                    SetRefundPoints(0);
                    Assert.Empty(CharacterSaves.Load(character)!.ClusterPassives ?? []);
                    Assert.Equal((ushort)0, CharacterSaves.Load(character)!.RefundPoints);

                    AssertCursor(null);
                    await global::Game.PickUpItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PickUpItem(
                            PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            clusterHeld.Id, 3, 0, 0));
                    AssertCursor(null);
                    Assert.Equal(clusterHeld.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);

                    var replacementC = inventory.Add(PlayerInventories.Cursor, 0, 0,
                        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Jewels/JewelInt")!));
                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(
                            PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            clusterCell, 0, 0, 3, 0, 0));
                    AssertCursor(replacementC);
                    Assert.Equal(clusterHeld.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);
                    await PutCursorInMain(replacementC, 2);

                    var replacementD = inventory.Add(PlayerInventories.Cursor, 0, 0,
                        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Jewels/JewelInt")!));
                    await global::Game.HeldOntoItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.ItemAction(
                            PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            clusterHeld.Id, 0, 3, 0), "cluster refund-point removal refusal");
                    AssertCursor(replacementD);
                    Assert.Equal(clusterHeld.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);
                    await PutCursorInMain(replacementD, 3);
                    Assert.Equal((ushort)0, self.RefundPoints);
                    Assert.Equal((ushort)0, CharacterSaves.Load(character)!.RefundPoints);
                    AssertAuraValues(expectedVitality, Assert.Single(self.Auras.Values));
                    Assert.Empty(self.Golems.All);
                    Assert.Empty(self.Summons.Fighters);

                    SetRefundPoints(1);
                    var pickupPackets = await Capture(() =>
                        global::Game.PickUpItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.PickUpItem(
                                PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                                clusterHeld.Id, 3, 0, 0)));
                    AssertInventoryPair(pickupPackets, PlayerInventories.PassiveJewels,
                        PlayerInventories.Cursor, clusterHeld.Id, 0, 0, clusterHeld.Id);
                    AssertCursor(clusterHeld);
                    Assert.Empty(inventory.Items(PlayerInventories.PassiveJewels));
                    Assert.Equal((ushort)0, self.RefundPoints);
                    Assert.Equal((ushort)0, CharacterSaves.Load(character)!.RefundPoints);
                    AssertAuraValues(expectedVitality, Assert.Single(self.Auras.Values));
                    Assert.Empty(self.Golems.All);

                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(
                            PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            clusterCell, 0, 0, 3, 0, 0));
                    AssertCursor(null);
                    Assert.Equal(clusterHeld.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);

                    SetRefundPoints(1);
                    var replacementE = inventory.Add(PlayerInventories.Cursor, 0, 0,
                        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Jewels/JewelInt")!));
                    var placeSwapPackets = await Capture(() =>
                        global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.PlaceItem(
                                PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                                clusterCell, 0, 0, 3, 0, 0)));
                    AssertInventoryPair(placeSwapPackets, PlayerInventories.PassiveJewels,
                        PlayerInventories.Cursor, clusterHeld.Id, replacementE.Id, replacementE.Id, clusterHeld.Id);
                    AssertCursor(clusterHeld);
                    Assert.Equal(replacementE.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);
                    Assert.Equal((ushort)0, self.RefundPoints);
                    Assert.Equal((ushort)0, CharacterSaves.Load(character)!.RefundPoints);
                    AssertAuraValues(expectedVitality, Assert.Single(self.Auras.Values));

                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(
                            PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                            clusterCell, 0, 0, 3, 0, 0));
                    AssertCursor(replacementE);
                    Assert.Equal(clusterHeld.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);

                    SetRefundPoints(1);
                    var heldSwapPackets = await Capture(() =>
                        global::Game.HeldOntoItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.ItemAction(
                                PlayerInventories.InventoryId(PlayerInventories.PassiveJewels),
                                clusterHeld.Id, 0, 3, 0), "legal held cluster removal"));
                    AssertInventoryPair(heldSwapPackets, PlayerInventories.PassiveJewels,
                        PlayerInventories.Cursor, clusterHeld.Id, replacementE.Id, replacementE.Id, clusterHeld.Id);
                    AssertCursor(clusterHeld);
                    Assert.Equal(replacementE.Id, Assert.Single(
                        inventory.Items(PlayerInventories.PassiveJewels),
                        item => item.X == clusterCell).Id);
                    Assert.Equal((ushort)0, self.RefundPoints);
                    Assert.Equal((ushort)0, CharacterSaves.Load(character)!.RefundPoints);
                    AssertAuraValues(expectedVitality, Assert.Single(self.Auras.Values));
                    Assert.Empty(self.Golems.All);
                    Assert.Empty(self.Summons.Fighters);
                }
                finally
                {
                    global::Game.AreaUpdateContext.Value = previousContext;
                    if (isolated is not null)
                    {
                        isolated.Closed = true;
                        PlayerInventories.Forget(character);
                        global::Game.InactiveItems.TryRemove(character, out _);
                    }
                }
            }

            async Task EquipmentSourceAuthority()
            {
                // Isolated actual-equipment authority: public pickup/place and the real 0xdd dispatch, one-handed
                // catalogue wands, real socketed Golem gems and a persistent real Astramentis requirement source.
                const string equipmentCharacter = "EquipmentSourceAuthority";
                var equipmentSchedule = new AreaClockSchedule();
                var equipmentUpdates = new PlayerUpdatesState(lifetime, equipmentSchedule);
                var previousEquipmentContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = equipmentUpdates;
                ClientState? equipment = null;
                try
                {
                    equipmentSchedule.At = 60;
                    var self = equipment = new ClientState(
                        new(1, "equipment-source-authority", 1000, 1000, 100, 100, null, []), 11, 1)
                    {
                        Character = equipmentCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = equipmentUpdates,
                        // Real base attributes stay below the Golem gems' requirements; Astramentis supplies the persistent
                        // remainder. Two Golems are the labelled capacity fixture, not a virtual-attribute shortcut.
                        Pools = new(100, 10000, 10000, 2, 0, Strength: 65, Dexterity: 65, Intelligence: 65),
                        LeagueModule = conn.LeagueModule,
                    };
                    self.Golems.Restored = true;
                    self.Summons.Restored = true;
                    self.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 2;

                    var inventory = global::Game.ItemsOf(self);
                    var wandType = Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!;
                    var wandA = ItemCatalog.Create(wandType);
                    var wandB = ItemCatalog.Create(wandType);
                    var flameGem = ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
                    var stoneGem = ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Gems/SkillGemSummonRockGolem")!);
                    var astramentis = new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!;
                    var a = inventory.Add(PlayerInventories.Weapon1, 0, 0, wandA with
                    {
                        Sockets = [new(SocketColour.White, flameGem)],
                    });
                    var b = inventory.Add(PlayerInventories.Offhand1, 0, 0, wandB with
                    {
                        Sockets = [new(SocketColour.White, stoneGem)],
                    });
                    const byte amuletRow = 5;
                    var amulet = inventory.Add(amuletRow, 0, 0, astramentis);
                    void Recalculate()
                    {
                        global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                        self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                        self.PlayerLife = self.Pools.Life;
                        self.PlayerMana = self.Pools.Mana;
                    }
                    Recalculate();

                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        Assert.Empty(self.Passives);
                        var equipment = inventory.EquipmentSnapshot();
                        return global::Game.PlayerSkillSourcesAt(self.Character, equipment,
                            global::Game.InactiveItems.GetValueOrDefault(self.Character), self.LeagueModule!,
                            [], global::Game.WornGrantedSkillsOf(self, equipment));
                    }
                    global::Game.PlayerSkillSource Source(string effect) => Assert.Single(
                        Sources(),
                        source => global::Game.SkillsFor(self).EffectId(source.Effect) == effect);
                    byte[] PickupMessage(byte row, uint itemId) => new LoginWriter()
                        .U16(InstanceItems.PickUpItemId)
                        .U32(PlayerInventories.InventoryId(row)).U32(itemId).U8(3).U8(0).U8(0).ToArray();
                    void AssertPromotionPickupWire(IReadOnlyList<byte[]> outputs, byte mainRow, uint picked, uint promoted)
                    {
                        uint mainInventory = PlayerInventories.InventoryId(mainRow);
                        uint cursorInventory = PlayerInventories.InventoryId(PlayerInventories.Cursor);
                        uint offhandInventory = PlayerInventories.InventoryId((byte)(mainRow + 1));
                        var changes = outputs.Where(packet => packet.Length >= 15
                                && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceItems.InventoryChangeId)
                            .Select(packet =>
                            {
                                uint inventory = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(3));
                                uint revision = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(7));
                                uint removedCount = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(11));
                                uint removedFirst = removedCount == 0
                                    ? 0
                                    : BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(15));
                                int addedCountAt = 15 + checked((int)removedCount * 4);
                                uint addedCount = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(addedCountAt));
                                uint addedFirst = addedCount == 0
                                    ? 0
                                    : BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(addedCountAt + 4));
                                return (Inventory: inventory, Revision: revision, RemovedFirst: removedFirst,
                                    AddedFirst: addedFirst);
                            })
                            .Where(change => change.Inventory == mainInventory || change.Inventory == cursorInventory
                                || change.Inventory == offhandInventory)
                            .ToArray();

                        Assert.Collection(changes,
                            change =>
                            {
                                Assert.Equal(mainInventory, change.Inventory);
                                Assert.Equal(picked, change.RemovedFirst);
                                Assert.Equal(0u, change.AddedFirst);
                            },
                            change =>
                            {
                                Assert.Equal(cursorInventory, change.Inventory);
                                Assert.Equal(0u, change.RemovedFirst);
                                Assert.Equal(picked, change.AddedFirst);
                            },
                            change =>
                            {
                                Assert.Equal(offhandInventory, change.Inventory);
                                Assert.Equal(promoted, change.RemovedFirst);
                                Assert.Equal(0u, change.AddedFirst);
                            },
                            change =>
                            {
                                Assert.Equal(mainInventory, change.Inventory);
                                Assert.Equal(0u, change.RemovedFirst);
                                Assert.Equal(promoted, change.AddedFirst);
                            });
                        Assert.Equal(changes[0].Revision + 1, changes[^1].Revision);
                    }
                    bool Met(global::Game.PlayerSkillSource source) =>
                        global::Game.SupportedSkill(self, source.Id).RequirementsMet;
                    bool LastSavedSource(ushort source) =>
                        JsonSerializer.Deserialize<GolemState.Saved[]>(
                            CharacterSaves.ModuleData(equipmentCharacter, "Game.Golems") ?? "[]")!
                        .Any(row => row.Skill == source);

                    // Row 14 is the inactive main hand. The public 0x28 handler must still promote its one-handed off
                    // hand without activating the set or reconciling the unchanged active sources.
                    var activeFlameBefore = Source("SummonFlameGolem");
                    var activeStoneBefore = Source("SummonStoneGolem");
                    var inactiveMain = inventory.Add(PlayerInventories.Weapon2, 0, 0, wandA);
                    var inactiveOff = inventory.Add(PlayerInventories.Offhand2, 0, 0, wandB);
                    var inactivePackets = new List<byte[]>();
                    var previousInactiveOutgoing = Program.Outgoing;
                    Program.Outgoing = (ciphers, bytes) =>
                    {
                        if (ReferenceEquals(ciphers, wire.Ciphers)) lock (inactivePackets) inactivePackets.Add(bytes.ToArray());
                        previousInactiveOutgoing?.Invoke(ciphers, bytes);
                    };
                    try
                    {
                        await global::Game.OnClientMessages("test", wire.Stream, wire.Ciphers,
                            [(InstanceItems.PickUpItemId, PickupMessage(PlayerInventories.Weapon2, inactiveMain.Id))], [], self);
                    }
                    finally { Program.Outgoing = previousInactiveOutgoing; }
                    Assert.Equal((byte)0, inventory.WeaponSet);
                    Assert.Equal(inactiveMain.Id, Assert.Single(inventory.Items(PlayerInventories.Cursor)).Id);
                    Assert.Equal(inactiveOff.Id, Assert.Single(inventory.Items(PlayerInventories.Weapon2)).Id);
                    Assert.Empty(inventory.Items(PlayerInventories.Offhand2));
                    Assert.Equal(activeFlameBefore.Identity, Source("SummonFlameGolem").Identity);
                    Assert.Equal(activeStoneBefore.Identity, Source("SummonStoneGolem").Identity);
                    AssertPromotionPickupWire(inactivePackets, PlayerInventories.Weapon2, inactiveMain.Id, inactiveOff.Id);
                    Assert.NotNull(inventory.Remove(PlayerInventories.Weapon2, inactiveOff.Id));
                    Assert.NotNull(inventory.Remove(PlayerInventories.Cursor, inactiveMain.Id));
                    Assert.Empty(inventory.Items(PlayerInventories.Weapon2));
                    Assert.Empty(inventory.Items(PlayerInventories.Offhand2));
                    Assert.Empty(inventory.Items(PlayerInventories.Cursor));

                    var flameSource = Source("SummonFlameGolem");
                    var stoneSource = Source("SummonStoneGolem");
                    Assert.True(Met(flameSource));
                    Assert.True(Met(stoneSource));
                    var flameDefinition = global::Game.GolemData.Find("SummonFlameGolem")!;
                    var stoneDefinition = global::Game.GolemData.Find("SummonStoneGolem")!;
                    uint flameId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        flameSource.Id, flameDefinition, (100, 100), arrival: true);
                    uint stoneId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        stoneSource.Id, stoneDefinition, (100, 100), arrival: true);
                    Assert.NotEqual(0u, flameId);
                    Assert.NotEqual(0u, stoneId);
                    var flameActor = self.Golems.All[flameId];
                    var stoneActor = self.Golems.All[stoneId];
                    var flameKey = flameSource.Identity;
                    var stoneKey = stoneSource.Identity;
                    var flameToken = self.GemSourceLifetimes.Current(flameKey);
                    var stoneToken = self.GemSourceLifetimes.Current(stoneKey);
                    Assert.NotNull(flameToken);
                    Assert.NotNull(stoneToken);
                    Assert.True(LastSavedSource(flameSource.Id));
                    Assert.True(LastSavedSource(stoneSource.Id));

                    var pickupGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(pickupGate.Wait(0));
                    Task<uint> stalePickupFresh = null!;
                    Task<uint> stalePickupReplacement = null!;
                    Task pickup = null!;
                    var pickupPackets = new List<byte[]>();
                    var previousPickupOutgoing = Program.Outgoing;
                    Program.Outgoing = (ciphers, bytes) =>
                    {
                        if (ReferenceEquals(ciphers, wire.Ciphers)) lock (pickupPackets) pickupPackets.Add(bytes.ToArray());
                        previousPickupOutgoing?.Invoke(ciphers, bytes);
                    };
                    try
                    {
                        try
                        {
                            stalePickupFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                                flameSource.Id, flameDefinition, (100, 100), arrival: true);
                            stalePickupReplacement = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                                flameSource.Id, flameDefinition, (100, 100), arrival: true, replacingOld: flameActor);
                            Assert.False(stalePickupFresh.IsCompleted);
                            Assert.False(stalePickupReplacement.IsCompleted);
                            pickup = global::Game.OnClientMessages("test", wire.Stream, wire.Ciphers,
                                [(InstanceItems.PickUpItemId, PickupMessage(PlayerInventories.Weapon1, a.Id))], [], self);
                            Assert.False(pickup.IsCompleted,
                                "the actual equipment pickup did not suspend after synchronous pickup, promotion and retirement");
                            Assert.Equal(a.Id, Assert.Single(inventory.Items(PlayerInventories.Cursor)).Id);
                            Assert.Equal(b.Id, Assert.Single(inventory.Items(PlayerInventories.Weapon1)).Id);
                            Assert.Empty(inventory.Items(PlayerInventories.Offhand1));
                            var canonical = Sources();
                            Assert.DoesNotContain(canonical, source => source.Identity == flameKey);
                            var promoted = Assert.Single(canonical, source => source.Identity == stoneKey);
                            Assert.Equal(stoneSource.Id, promoted.Id);
                            Assert.True(Met(promoted));
                            Assert.DoesNotContain(flameId, self.Golems.All.Keys);
                            Assert.False(self.Minions.Owns(flameId, flameActor.Monster));
                            Assert.Null(self.GemSourceLifetimes.Current(flameKey));
                            Assert.False(LastSavedSource(flameSource.Id));
                            Assert.Same(stoneActor, self.Golems.All[stoneId]);
                            Assert.True(self.Minions.Owns(stoneId, stoneActor.Monster));
                            Assert.Same(stoneToken, self.GemSourceLifetimes.Current(stoneKey));
                            Assert.True(self.Golems.Buffs.ContainsKey(stoneDefinition.Buff));
                            Assert.True(LastSavedSource(stoneSource.Id));
                        }
                        finally { pickupGate.Release(); }
                        Assert.Equal(0u, await stalePickupFresh);
                        Assert.Equal(0u, await stalePickupReplacement);
                        await pickup;
                        AssertPromotionPickupWire(pickupPackets, PlayerInventories.Weapon1, a.Id, b.Id);
                    }
                    finally
                    {
                        Program.Outgoing = previousPickupOutgoing;
                    }

                    // A failed public placement captures its old source but cannot retire it. The held A remains on the
                    // cursor and B's promoted actor, lifetime and owner buff continue unchanged.
                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.BodyArmour),
                            0, 0, 0, 3, 0, 0));
                    Assert.Equal(a.Id, Assert.Single(inventory.Items(PlayerInventories.Cursor)).Id);
                    Assert.Equal(b.Id, Assert.Single(inventory.Items(PlayerInventories.Weapon1)).Id);
                    Assert.Same(stoneActor, self.Golems.All[stoneId]);
                    Assert.Same(stoneToken, self.GemSourceLifetimes.Current(stoneKey));

                    // Rewearing A displaces B. A's old physical lifetime stays gone; B retires before the real cursor
                    // output can resume. A fresh current preparation then creates a new actor from the same item id.
                    var placeGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(placeGate.Wait(0));
                    Task<uint> stalePlaceFresh = null!;
                    Task<uint> stalePlaceReplacement = null!;
                    Task replacing = null!;
                    try
                    {
                        stalePlaceFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            stoneSource.Id, stoneDefinition, (100, 100), arrival: true);
                        stalePlaceReplacement = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            stoneSource.Id, stoneDefinition, (100, 100), arrival: true, replacingOld: stoneActor);
                        Assert.False(stalePlaceFresh.IsCompleted);
                        Assert.False(stalePlaceReplacement.IsCompleted);
                        replacing = global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.Weapon1),
                                0, 0, 0, 3, 0, 0));
                        Assert.False(replacing.IsCompleted,
                            "the actual equipment placement did not suspend after the replaced host retired");
                        Assert.Equal(a.Id, Assert.Single(inventory.Items(PlayerInventories.Weapon1)).Id);
                        Assert.Equal(b.Id, Assert.Single(inventory.Items(PlayerInventories.Cursor)).Id);
                        Assert.DoesNotContain(stoneId, self.Golems.All.Keys);
                        Assert.False(self.Minions.Owns(stoneId, stoneActor.Monster));
                        Assert.Null(self.GemSourceLifetimes.Current(stoneKey));
                        Assert.False(LastSavedSource(stoneSource.Id));
                        Assert.DoesNotContain(self.Golems.Buffs.Keys, buff => buff == stoneDefinition.Buff);
                        var rewearSource = Source("SummonFlameGolem");
                        Assert.True(Met(rewearSource));
                        Assert.Equal(flameSource.Id, rewearSource.Id);
                        Assert.Null(self.GemSourceLifetimes.Current(flameKey));
                    }
                    finally { placeGate.Release(); }
                    Assert.Equal(0u, await stalePlaceFresh);
                    Assert.Equal(0u, await stalePlaceReplacement);
                    await replacing;
                    var rewornSource = Source("SummonFlameGolem");
                    uint rewornId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        rewornSource.Id, flameDefinition, (100, 100), arrival: true);
                    Assert.NotEqual(0u, rewornId);
                    var rewornActor = self.Golems.All[rewornId];
                    Assert.True(LastSavedSource(rewornSource.Id));

                    // Put B into the inactive second set, then use the actual 0xdd dispatch. The old active A actor and
                    // lifetime end before the first row output; a swap back before that output cannot revive the token.
                    var heldB = Assert.Single(inventory.Items(PlayerInventories.Cursor));
                    Assert.Equal(b.Id, heldB.Id);
                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.Weapon2),
                            0, 0, 0, 3, 0, 0));
                    Assert.Equal(b.Id, Assert.Single(inventory.Items(PlayerInventories.Weapon2)).Id);
                    var activeA = Source("SummonFlameGolem");
                    var activeAKey = activeA.Identity;
                    var activeAToken = self.GemSourceLifetimes.Current(activeAKey);
                    Assert.NotNull(activeAToken);
                    var swapGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(swapGate.Wait(0));
                    Task<uint> staleSwapFresh = null!;
                    Task<uint> staleSwapReplacement = null!;
                    Task toSecondSet = null!;
                    Task toFirstSet = null!;
                    try
                    {
                        staleSwapFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            activeA.Id, flameDefinition, (100, 100), arrival: true);
                        staleSwapReplacement = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            activeA.Id, flameDefinition, (100, 100), arrival: true, replacingOld: rewornActor);
                        Assert.False(staleSwapFresh.IsCompleted);
                        Assert.False(staleSwapReplacement.IsCompleted);
                        toSecondSet = global::Game.OnClientMessages("test", wire.Stream, wire.Ciphers,
                            [(InstanceProtocol.SwapWeaponsId, new byte[] { 0xdd, 0, 1 })], [], self);
                        Assert.False(toSecondSet.IsCompleted,
                            "the actual 0xdd dispatch did not suspend after its four-row mutation");
                        Assert.Equal((byte)1, inventory.WeaponSet);
                        Assert.Equal(b.Id, Assert.Single(inventory.Items(PlayerInventories.Weapon1)).Id);
                        Assert.Empty(inventory.Items(PlayerInventories.Offhand1));
                        Assert.Equal(a.Id, Assert.Single(inventory.Items(PlayerInventories.Weapon2)).Id);
                        Assert.Empty(inventory.Items(PlayerInventories.Offhand2));
                        Assert.DoesNotContain(rewornId, self.Golems.All.Keys);
                        Assert.False(self.Minions.Owns(rewornId, rewornActor.Monster));
                        Assert.Null(self.GemSourceLifetimes.Current(activeAKey));
                        Assert.True(Met(Source("SummonStoneGolem")));

                        toFirstSet = global::Game.OnClientMessages("test", wire.Stream, wire.Ciphers,
                            [(InstanceProtocol.SwapWeaponsId, new byte[] { 0xdd, 0, 0 })], [], self);
                        Assert.False(toFirstSet.IsCompleted);
                        Assert.Equal((byte)0, inventory.WeaponSet);
                        Assert.Equal(a.Id, Assert.Single(inventory.Items(PlayerInventories.Weapon1)).Id);
                        Assert.Empty(inventory.Items(PlayerInventories.Offhand1));
                        Assert.Equal(b.Id, Assert.Single(inventory.Items(PlayerInventories.Weapon2)).Id);
                        Assert.Empty(inventory.Items(PlayerInventories.Offhand2));
                        Assert.Null(self.GemSourceLifetimes.Current(activeAKey));
                        Assert.Empty(self.Golems.All);
                    }
                    finally { swapGate.Release(); }
                    Assert.Equal(0u, await staleSwapFresh);
                    Assert.Equal(0u, await staleSwapReplacement);
                    await toSecondSet;
                    await toFirstSet;
                    var swappedBackSource = Source("SummonFlameGolem");
                    uint swappedBackId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        swappedBackSource.Id, flameDefinition, (100, 100), arrival: true);
                    Assert.NotEqual(0u, swappedBackId);
                    var swappedBackActor = self.Golems.All[swappedBackId];
                    var swappedBackToken = self.GemSourceLifetimes.Current(swappedBackSource.Identity);
                    Assert.NotNull(swappedBackToken);

                    var sameSetGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(sameSetGate.Wait(0));
                    Task sameSet = null!;
                    try
                    {
                        sameSet = global::Game.OnClientMessages("test", wire.Stream, wire.Ciphers,
                            [(InstanceProtocol.SwapWeaponsId, new byte[] { 0xdd, 0, 0 })], [], self);
                        Assert.False(sameSet.IsCompleted);
                        Assert.Equal((byte)0, inventory.WeaponSet);
                        Assert.Same(swappedBackActor, self.Golems.All[swappedBackId]);
                        Assert.Same(swappedBackToken, self.GemSourceLifetimes.Current(swappedBackSource.Identity));
                    }
                    finally { sameSetGate.Release(); }
                    await sameSet;
                    Assert.Same(swappedBackActor, self.Golems.All[swappedBackId]);
                    Assert.Same(swappedBackToken, self.GemSourceLifetimes.Current(swappedBackSource.Identity));

                    // Both real hosts are active together. Removing the actual Astramentis makes their actual gem
                    // requirements fail while the hosts stay equipped, so both actors and lifetimes end before output.
                    var inactiveB = Assert.Single(inventory.Items(PlayerInventories.Weapon2));
                    Assert.NotNull(inventory.Remove(PlayerInventories.Weapon2, inactiveB.Id));
                    var bothOffhand = inventory.Add(PlayerInventories.Offhand1, 0, 0, inactiveB.Item);
                    Recalculate();
                    var bothFlame = Source("SummonFlameGolem");
                    var bothStone = Source("SummonStoneGolem");
                    Assert.True(Met(bothFlame));
                    Assert.True(Met(bothStone));
                    uint bothStoneId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        bothStone.Id, stoneDefinition, (100, 100), arrival: true);
                    Assert.NotEqual(0u, bothStoneId);
                    var bothStoneActor = self.Golems.All[bothStoneId];
                    var bothFlameToken = self.GemSourceLifetimes.Current(bothFlame.Identity);
                    var bothStoneToken = self.GemSourceLifetimes.Current(bothStone.Identity);
                    Assert.NotNull(bothFlameToken);
                    Assert.NotNull(bothStoneToken);

                    var amuletGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(amuletGate.Wait(0));
                    Task<uint> staleAttributeFresh = null!;
                    Task<uint> staleAttributeReplacement = null!;
                    Task removeAmulet = null!;
                    try
                    {
                        staleAttributeFresh = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            bothFlame.Id, flameDefinition, (100, 100), arrival: true);
                        staleAttributeReplacement = global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                            bothFlame.Id, flameDefinition, (100, 100), arrival: true, replacingOld: swappedBackActor);
                        Assert.False(staleAttributeFresh.IsCompleted);
                        Assert.False(staleAttributeReplacement.IsCompleted);
                        removeAmulet = global::Game.PickUpItem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.PickUpItem(PlayerInventories.InventoryId(amuletRow),
                                amulet.Id, 3, 0, 0));
                        Assert.False(removeAmulet.IsCompleted,
                            "the actual Astramentis pickup did not suspend after retiring both invalid sources");
                        Assert.Equal(amulet.Id, Assert.Single(inventory.Items(PlayerInventories.Cursor)).Id);
                        Assert.Equal(a.Id, Assert.Single(inventory.Items(PlayerInventories.Weapon1)).Id);
                        Assert.Equal(bothOffhand.Id, Assert.Single(inventory.Items(PlayerInventories.Offhand1)).Id);
                        var unqualified = Sources();
                        var flameHost = Assert.Single(unqualified, source => source.Identity == bothFlame.Identity);
                        var stoneHost = Assert.Single(unqualified, source => source.Identity == bothStone.Identity);
                        Assert.False(flameHost.Active);
                        Assert.False(stoneHost.Active);
                        var inactiveHosts = global::Game.InactiveItems.GetValueOrDefault(self.Character) ?? [];
                        Assert.Contains(flameHost.Identity.Host, inactiveHosts);
                        Assert.Contains(stoneHost.Identity.Host, inactiveHosts);
                        Assert.False(Met(flameHost));
                        Assert.False(Met(stoneHost));
                        Assert.Empty(self.Golems.All);
                        Assert.Null(self.GemSourceLifetimes.Current(bothFlame.Identity));
                        Assert.Null(self.GemSourceLifetimes.Current(bothStone.Identity));
                        Assert.DoesNotContain(self.Golems.Buffs.Keys,
                            buff => buff == flameDefinition.Buff || buff == stoneDefinition.Buff);
                        Assert.False(LastSavedSource(bothFlame.Id));
                        Assert.False(LastSavedSource(bothStone.Id));
                    }
                    finally { amuletGate.Release(); }
                    Assert.Equal(0u, await staleAttributeFresh);
                    Assert.Equal(0u, await staleAttributeReplacement);
                    await removeAmulet;

                    await global::Game.PlaceItem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.PlaceItem(PlayerInventories.InventoryId(amuletRow),
                            0, 0, 0, 3, 0, 0));
                    var eligibleAgain = Source("SummonFlameGolem");
                    Assert.True(Met(eligibleAgain));
                    Assert.Empty(self.Golems.All);
                    Assert.Null(self.GemSourceLifetimes.Current(eligibleAgain.Identity));
                    uint eligibleAgainId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        eligibleAgain.Id, flameDefinition, (100, 100), arrival: true);
                    Assert.NotEqual(0u, eligibleAgainId);
                    Assert.Contains(eligibleAgainId, self.Golems.All.Keys);
                }
                finally
                {
                    global::Game.AreaUpdateContext.Value = previousEquipmentContext;
                    if (equipment is not null)
                    {
                        equipment.Closed = true;
                        PlayerInventories.Forget(equipmentCharacter);
                        global::Game.InactiveItems.TryRemove(equipmentCharacter, out _);
                    }
                }
            }

            async Task DependentOwnerRetirement()
            {
                // Actual public source removal must end auras, Petrified Blood, placed skills and the last Holy Relic
                // before the first inventory/equipment output can resume. The fixture uses catalogue gems and real
                // production activation/create paths; only the deferred-life seed is a labelled existing-state control.
                const string ownerCharacter = "DependentOwnerRetirement";
                var ownerSchedule = new AdvancingAreaClockSchedule();
                var ownerUpdates = new PlayerUpdatesState(lifetime, ownerSchedule);
                var previousOwnerContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = ownerUpdates;
                ClientState? owner = null;
                try
                {
                    ownerSchedule.At = 100;
                    var self = owner = new ClientState(
                        new(1, "dependent-owner-retirement", 1000, 1000, 100, 100, null, []), 10, 1)
                    {
                        Character = ownerCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = ownerUpdates,
                        Pools = new(100, 10000, 10000, 2, 0),
                        LeagueModule = conn.LeagueModule,
                    };
                    self.Golems.Restored = true;
                    self.Summons.Restored = true;
                    self.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                    self.PlayerStats[Program.Stats.Key("number_of_relics_allowed")!.Value] = 1;
                    self.Walk.Teleport(100, 100, ownerSchedule.At);

                    Item Gem(string name)
                    {
                        var type = Program.Items.FindGem(name)!.Value;
                        return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index };
                    }
                    var inventory = global::Game.ItemsOf(self);
                    var bodyHost = inventory.Add(PlayerInventories.BodyArmour, 0, 0,
                        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!)
                        with
                        {
                            Sockets = new[] { "Clarity", "Vitality", "Petrified Blood", "Stormblast Mine" }
                                .Select(name => new ItemSocket(SocketColour.White, Gem(name))).ToArray(),
                            Links = [4],
                        });
                    var weaponHost = inventory.Add(PlayerInventories.Weapon1, 0, 0,
                        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!)
                        with
                        {
                            Sockets = new[] { "Fire Trap", "Summon Holy Relic", "Summon Flame Golem" }
                                .Select(name => new ItemSocket(SocketColour.White, Gem(name))).ToArray(),
                            Links = [3],
                        });
                    const byte amuletRow = 5;
                    inventory.Add(amuletRow, 0, 0, new UniqueCatalog(Program.Items).Create(
                        "Astramentis", new Random(504))!);
                    void Recalculate()
                    {
                        global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                        self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                        self.PlayerLife = self.Pools.Life;
                        self.PlayerMana = self.Pools.Mana;
                    }
                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        var equipment = inventory.EquipmentSnapshot();
                        return global::Game.PlayerSkillSourcesAt(self.Character, equipment,
                            global::Game.InactiveItems.GetValueOrDefault(self.Character), self.LeagueModule!,
                            [], global::Game.WornGrantedSkillsOf(self, equipment));
                    }
                    global::Game.PlayerSkillSource Source(string effect) => Assert.Single(
                        Sources(), source => global::Game.SkillsFor(self).EffectId(source.Effect) == effect);
                    InstanceItems.SocketGem Target(byte row, uint host, uint socket) =>
                        new(PlayerInventories.InventoryId(row), host, socket);
                    Dictionary<uint, int> PlayerStats()
                    {
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            return new(global::Game.CharacterPlayerStats(self));
                    }
                    Recalculate();

                    Assert.All(Sources(), source =>
                    {
                        var supported = global::Game.SupportedSkill(self, source.Id);
                        var unmet = global::Game.GemRequirementsUnmet(self, supported.SourceGem);
                        Assert.True(source.Active,
                            $"{source.Name} source is inactive; actual GemRequirementsUnmet: {unmet ?? "null"}");
                        Assert.True(supported.RequirementsMet,
                            $"{source.Name} requirements are not met; actual GemRequirementsUnmet: {unmet ?? "null"}");
                    });

                    var claritySource = Source("Clarity");
                    var clarityReservation = global::Game.SupportedReservationCost(self, claritySource.Id);
                    Assert.NotNull(clarityReservation);
                    var statsBeforeClarity = PlayerStats();
                    Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self, claritySource.Id,
                        claritySource.Effect, claritySource.Name, clarityReservation!.Value,
                        claritySource.Level, claritySource.Quality));
                    claritySource = Source("Clarity");
                    var clarityAura = Assert.Single(self.Auras.Values, aura => aura.EffectId == "Clarity");
                    Assert.Equal(claritySource.Id, clarityAura.SkillId);
                    Assert.Contains("Clarity", CharacterSaves.Load(ownerCharacter)!.ActiveAuras!);
                    Assert.Contains(PlayerStats(), pair => !statsBeforeClarity.TryGetValue(pair.Key, out int before)
                        || before != pair.Value);
                    Assert.True(global::Game.ReservedMana(self) > 0);

                    var vitalitySource = Source("Vitality");
                    var vitalityReservation = global::Game.SupportedReservationCost(self, vitalitySource.Id);
                    Assert.NotNull(vitalityReservation);
                    Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self, vitalitySource.Id,
                        vitalitySource.Effect, vitalitySource.Name, vitalityReservation!.Value,
                        vitalitySource.Level, vitalitySource.Quality));
                    vitalitySource = Source("Vitality");
                    Assert.Single(self.Auras.Values, aura => aura.EffectId == "Vitality");
                    uint reservedWithBoth = (uint)global::Game.ReservedMana(self);
                    Assert.Equal(2, self.Auras.Count);

                    var clarityPackets = new List<byte[]>();
                    var previousClarityOutput = Program.Outgoing;
                    Program.Outgoing = (seen, bytes) =>
                    {
                        if (ReferenceEquals(seen, wire.Ciphers)) lock (clarityPackets) clarityPackets.Add(bytes.ToArray());
                        previousClarityOutput?.Invoke(seen, bytes);
                    };
                    Task removeClarity;
                    var clarityGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(clarityGate.Wait(0));
                    try
                    {
                        try
                        {
                            removeClarity = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                                Target(PlayerInventories.BodyArmour, bodyHost.Id, 0));
                            Assert.False(removeClarity.IsCompleted);
                            Assert.DoesNotContain(claritySource.Id, self.Auras.Keys);
                            Assert.Contains(vitalitySource.Id, self.Auras.Keys);
                            Assert.True(global::Game.ReservedMana(self) < reservedWithBoth);
                            Assert.DoesNotContain("Clarity", CharacterSaves.Load(ownerCharacter)!.ActiveAuras!);
                            Assert.Contains("Vitality", CharacterSaves.Load(ownerCharacter)!.ActiveAuras!);
                        }
                        finally { clarityGate.Release(); }
                        await removeClarity;
                    }
                    finally { Program.Outgoing = previousClarityOutput; }
                    var clarityRemoval = InstanceEntities.RemoveBuff(Program.PlayerId, clarityAura.Buff);
                    Assert.Contains(clarityPackets, packet => packet.AsSpan().IndexOf(clarityRemoval) >= 0);

                    await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self,
                        Target(PlayerInventories.BodyArmour, bodyHost.Id, 0));
                    Assert.True(Source("Clarity").Active);
                    Assert.DoesNotContain(claritySource.Id, self.Auras.Keys);
                    Assert.DoesNotContain("Clarity", CharacterSaves.Load(ownerCharacter)!.ActiveAuras!);

                    // Reinsertion alone stays inactive. Activate the real Clarity source, then remove it again and let
                    // the first old Actor update observe the stale numeric id while the replacement source and aura are
                    // committed reentrantly behind that send gate. The later legacy callback must leave this new owner alone.
                    var currentClaritySource = Source("Clarity");
                    var currentClarityReservation = global::Game.SupportedReservationCost(self, currentClaritySource.Id);
                    Assert.NotNull(currentClarityReservation);
                    Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                        currentClaritySource.Id, currentClaritySource.Effect, currentClaritySource.Name,
                        currentClarityReservation!.Value, currentClaritySource.Level, currentClaritySource.Quality));
                    var currentClarityAura = Assert.Single(self.Auras.Values, aura => aura.EffectId == "Clarity");
                    Assert.NotSame(clarityAura, currentClarityAura);
                    Assert.Contains("Clarity", CharacterSaves.Load(ownerCharacter)!.ActiveAuras!);
                    var currentStatsBeforeStale = PlayerStats();
                    uint currentReservedBeforeStale = (uint)global::Game.ReservedMana(self);
                    uint currentManaBeforeStale = self.PlayerMana;
                    var vitalityAura = Assert.Single(self.Auras.Values, aura => aura.EffectId == "Vitality");

                    var stalePackets = new List<byte[]>();
                    var previousStaleOutput = Program.Outgoing;
                    Task staleRemoveClarity = null!;
                    Task staleReinsert = null!;
                    Task<bool> staleReactivate = null!;
                    global::Game.PlayerSkillSource? staleReplacementSource = null;
                    AuraCatalog.Aura? staleReplacementAura = null;
                    Dictionary<uint, int>? staleStatsBeforeLegacy = null;
                    bool staleActorSeen = false, staleGoneAbsentAtActor = false, staleSavedBeforeLegacy = false;
                    bool staleVitalityBeforeLegacy = false, staleCurrentWorkAfterReactivation = false;
                    byte staleActorGoneCount = 0;
                    uint staleReservedBeforeLegacy = 0, staleManaBeforeLegacy = 0;
                    Program.Outgoing = (seen, bytes) =>
                    {
                        if (ReferenceEquals(seen, wire.Ciphers))
                        {
                            byte[] wireBytes = bytes.ToArray();
                            lock (stalePackets) stalePackets.Add(wireBytes);
                            int actorAt = -1;
                            for (int at = 0; !staleActorSeen && at + 19 <= wireBytes.Length; at++)
                                if (BinaryPrimitives.ReadUInt16BigEndian(wireBytes.AsSpan(at)) == InstanceEntities.ComponentUpdateId
                                    && BinaryPrimitives.ReadUInt32BigEndian(wireBytes.AsSpan(at + 2)) == Program.PlayerId
                                    && BinaryPrimitives.ReadUInt32BigEndian(wireBytes.AsSpan(at + 6)) == 0
                                    && BinaryPrimitives.ReadUInt16BigEndian(wireBytes.AsSpan(at + 10)) == 0
                                    && wireBytes[at + 14] == PlayerObject.ActorComponentIndex
                                    && (wireBytes[at + 15] & 0x20) != 0
                                    && wireBytes[at + 18] == 1
                                    && !self.ClientSkillIds.Contains(currentClaritySource.Id))
                                {
                                    actorAt = at;
                                    break;
                                }
                            if (actorAt >= 0)
                            {
                                staleActorSeen = true;
                                staleActorGoneCount = wireBytes[actorAt + 18];
                                staleGoneAbsentAtActor = !self.ClientSkillIds.Contains(currentClaritySource.Id)
                                    && Sources().All(source => source.Id != currentClaritySource.Id)
                                    && !self.Auras.ContainsKey(currentClaritySource.Id);

                                // Disarm before either real handler can publish reentrantly. Both commit synchronously
                                // before their first gate wait; the observer never awaits or releases either task.
                                staleReinsert = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self,
                                    Target(PlayerInventories.BodyArmour, bodyHost.Id, 0));
                                var replacementSource = Source("Clarity");
                                staleReplacementSource = replacementSource;
                                staleReactivate = global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                                    replacementSource.Id, replacementSource.Effect, replacementSource.Name,
                                    currentClarityReservation.Value, replacementSource.Level, replacementSource.Quality);
                                staleReplacementAura = self.Auras.GetValueOrDefault(replacementSource.Id);
                                staleReservedBeforeLegacy = (uint)global::Game.ReservedMana(self);
                                staleManaBeforeLegacy = self.PlayerMana;
                                staleStatsBeforeLegacy = PlayerStats();
                                staleSavedBeforeLegacy =
                                    CharacterSaves.Load(ownerCharacter)!.ActiveAuras!.Contains("Clarity");
                                staleCurrentWorkAfterReactivation =
                                    global::Game.CurrentGameRules && global::Game.CurrentGameWork;
                                staleVitalityBeforeLegacy = self.Auras.GetValueOrDefault(vitalitySource.Id) is { } vitality
                                    && vitality.SkillId == vitalityAura.SkillId
                                    && vitality.Effect == vitalityAura.Effect
                                    && vitality.Buff == vitalityAura.Buff
                                    && vitality.SourceBuff == vitalityAura.SourceBuff
                                    && vitality.ManaFlat == vitalityAura.ManaFlat
                                    && vitality.ManaPercent == vitalityAura.ManaPercent
                                    && vitality.Level == vitalityAura.Level
                                    && vitality.Quality == vitalityAura.Quality;
                            }
                        }
                        previousStaleOutput?.Invoke(seen, bytes);
                    };
                    try
                    {
                        staleRemoveClarity = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                            Target(PlayerInventories.BodyArmour, bodyHost.Id, 0));
                        await staleRemoveClarity;
                        Assert.True(staleReinsert is not null, "the stale Actor update did not start source reinsertion");
                        await staleReinsert;
                        Assert.True(staleReactivate is not null, "the replacement Clarity was not reactivated");
                        Assert.True(await staleReactivate);
                    }
                    finally { Program.Outgoing = previousStaleOutput; }
                    Assert.True(staleActorSeen);
                    Assert.True(staleGoneAbsentAtActor);
                    Assert.Equal((byte)1, staleActorGoneCount);
                    Assert.NotNull(staleReplacementSource);
                    var replacementSource = staleReplacementSource!;
                    Assert.Equal(currentClaritySource.Id, replacementSource.Id);
                    Assert.Equal(currentClaritySource.Identity, replacementSource.Identity);
                    var replacementAura = Assert.IsType<AuraCatalog.Aura>(staleReplacementAura);
                    Assert.NotSame(currentClarityAura, replacementAura);
                    Assert.NotEqual(currentClarityAura.Buff, replacementAura.Buff);
                    Assert.NotEqual(currentClarityAura.SourceBuff, replacementAura.SourceBuff);
                    var currentAuraAfterStale = Assert.Single(self.Auras.Values, aura => aura.EffectId == "Clarity");
                    Assert.Equal(replacementAura.SkillId, currentAuraAfterStale.SkillId);
                    Assert.Equal(replacementAura.Effect, currentAuraAfterStale.Effect);
                    Assert.Equal(replacementAura.Buff, currentAuraAfterStale.Buff);
                    Assert.Equal(replacementAura.SourceBuff, currentAuraAfterStale.SourceBuff);
                    Assert.Equal(replacementAura.ManaFlat, currentAuraAfterStale.ManaFlat);
                    Assert.Equal(replacementAura.ManaPercent, currentAuraAfterStale.ManaPercent);
                    Assert.Equal(replacementAura.Level, currentAuraAfterStale.Level);
                    Assert.Equal(replacementAura.Quality, currentAuraAfterStale.Quality);
                    Assert.True(global::Game.SupportedSkill(self, currentClaritySource.Id).RequirementsMet);
                    Assert.Contains("Clarity", CharacterSaves.Load(ownerCharacter)!.ActiveAuras!);
                    Assert.True(staleSavedBeforeLegacy);
                    Assert.Equal(staleReservedBeforeLegacy, (uint)global::Game.ReservedMana(self));
                    Assert.Equal(staleManaBeforeLegacy, self.PlayerMana);
                    Assert.Equal(currentReservedBeforeStale, (uint)global::Game.ReservedMana(self));
                    Assert.Equal(currentManaBeforeStale, self.PlayerMana);
                    Assert.True(staleCurrentWorkAfterReactivation);
                    Assert.True(staleVitalityBeforeLegacy);
                    var vitalityAfterStale = Assert.Single(self.Auras.Values, aura => aura.EffectId == "Vitality");
                    Assert.Equal(vitalityAura.SkillId, vitalityAfterStale.SkillId);
                    Assert.Equal(vitalityAura.Effect, vitalityAfterStale.Effect);
                    Assert.Equal(vitalityAura.Buff, vitalityAfterStale.Buff);
                    Assert.Equal(vitalityAura.SourceBuff, vitalityAfterStale.SourceBuff);
                    Assert.Equal(vitalityAura.ManaFlat, vitalityAfterStale.ManaFlat);
                    Assert.Equal(vitalityAura.ManaPercent, vitalityAfterStale.ManaPercent);
                    Assert.Equal(vitalityAura.Level, vitalityAfterStale.Level);
                    Assert.Equal(vitalityAura.Quality, vitalityAfterStale.Quality);
                    Assert.Contains("Vitality", CharacterSaves.Load(ownerCharacter)!.ActiveAuras!);
                    Assert.NotNull(staleStatsBeforeLegacy);
                    var currentStatsAfterStale = PlayerStats();
                    Assert.True(currentStatsBeforeStale.Count == staleStatsBeforeLegacy.Count
                        && currentStatsBeforeStale.All(pair => staleStatsBeforeLegacy.TryGetValue(pair.Key, out int value)
                            && value == pair.Value));
                    Assert.True(staleStatsBeforeLegacy.Count == currentStatsAfterStale.Count
                        && staleStatsBeforeLegacy.All(pair => currentStatsAfterStale.TryGetValue(pair.Key, out int value)
                            && value == pair.Value));
                    Assert.DoesNotContain(stalePackets, packet =>
                        packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(Program.PlayerId, replacementAura.Buff)) >= 0
                        || packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(Program.PlayerId,
                            replacementAura.SourceBuff ?? 0)) >= 0);

                    // Supplemental control: a retired/cancelled output generation cannot let a retained legacy callback
                    // mutate the real current owner, its persisted activation or its reservation even when the current
                    // canonical source is valid.
                    var guardedQueue = self.Minions.OutputQueue.Count;
                    var guardedReserved = (uint)global::Game.ReservedMana(self);
                    var guardedMana = self.PlayerMana;
                    var guardedStats = PlayerStats();
                    var guardedAura = self.Auras[currentClaritySource.Id];
                    var previousOutputGuard = Program.ProjectileOutputGuard.Value;
                    Program.ProjectileOutputGuard.Value = () => false;
                    try
                    {
                        await global::Game.EndSkillEffects("test", wire.Stream, wire.Ciphers, self,
                            [currentClaritySource.Id]);
                    }
                    finally { Program.ProjectileOutputGuard.Value = previousOutputGuard; }
                    var guardedAuraAfter = self.Auras[currentClaritySource.Id];
                    Assert.Equal(guardedAura.SkillId, guardedAuraAfter.SkillId);
                    Assert.Equal(guardedAura.Effect, guardedAuraAfter.Effect);
                    Assert.Equal(guardedAura.Buff, guardedAuraAfter.Buff);
                    Assert.Equal(guardedAura.SourceBuff, guardedAuraAfter.SourceBuff);
                    Assert.Equal(guardedAura.ManaFlat, guardedAuraAfter.ManaFlat);
                    Assert.Equal(guardedAura.ManaPercent, guardedAuraAfter.ManaPercent);
                    Assert.Equal(guardedAura.Level, guardedAuraAfter.Level);
                    Assert.Equal(guardedAura.Quality, guardedAuraAfter.Quality);
                    Assert.Contains("Clarity", CharacterSaves.Load(ownerCharacter)!.ActiveAuras!);
                    Assert.Equal(guardedQueue, self.Minions.OutputQueue.Count);
                    Assert.Equal(guardedReserved, (uint)global::Game.ReservedMana(self));
                    Assert.Equal(guardedMana, self.PlayerMana);
                    var guardedStatsAfter = PlayerStats();
                    Assert.True(guardedStats.Count == guardedStatsAfter.Count
                        && guardedStats.All(pair => guardedStatsAfter.TryGetValue(pair.Key, out int value)
                            && value == pair.Value));

                    await global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                        Target(PlayerInventories.BodyArmour, bodyHost.Id, 1));
                    Assert.DoesNotContain(self.Auras.Values, aura => aura.EffectId == "Vitality");
                    await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self,
                        Target(PlayerInventories.BodyArmour, bodyHost.Id, 1));

                    var petrifiedSource = Source("PetrifiedBlood");
                    var petrifiedReservation = global::Game.SupportedReservationCost(self, petrifiedSource.Id);
                    Assert.NotNull(petrifiedReservation);
                    Assert.True(await global::Game.TogglePetrifiedBlood("test", wire.Stream, wire.Ciphers, self,
                        petrifiedSource.Id, petrifiedSource.Effect, petrifiedSource.Name,
                        petrifiedReservation!.Value, petrifiedSource.Level, petrifiedSource.Quality));
                    Assert.True(self.PetrifiedBlood.Active);
                    Assert.True(CharacterSaves.Load(ownerCharacter)!.PetrifiedBlood);
                    self.PetrifiedBlood.AddDeferred(ownerSchedule.At, 40);
                    Assert.True(self.PetrifiedBlood.Display(ownerSchedule.At + 1).PerMinute > 0);
                    uint reservedWithPetrified = (uint)global::Game.ReservedMana(self);
                    Task removePetrified;
                    var petrifiedGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(petrifiedGate.Wait(0));
                    try
                    {
                        removePetrified = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                            Target(PlayerInventories.BodyArmour, bodyHost.Id, 2));
                        Assert.False(removePetrified.IsCompleted);
                        Assert.False(self.PetrifiedBlood.Active);
                        Assert.False(CharacterSaves.Load(ownerCharacter)!.PetrifiedBlood);
                        Assert.True(global::Game.ReservedMana(self) < reservedWithPetrified);
                        Assert.True(self.PetrifiedBlood.Display(ownerSchedule.At + 1).PerMinute > 0);
                    }
                    finally { petrifiedGate.Release(); }
                    await removePetrified;
                    Assert.True(self.PetrifiedBlood.Take(ownerSchedule.At + PetrifiedBlood.DeferSeconds) > 0);
                    await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self,
                        Target(PlayerInventories.BodyArmour, bodyHost.Id, 2));
                    global::Game.ReconcileOwnAuras(self, validateSkills: true);
                    Assert.False(self.PetrifiedBlood.Active);
                    Assert.False(CharacterSaves.Load(ownerCharacter)!.PetrifiedBlood);

                    var mineSource = Source("StormblastMine");
                    var trapSource = Source("FireTrap");
                    ownerSchedule.At = 110;
                    var mineCast = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self, mineSource.Id,
                        (100, 100), (105, 105));
                    ownerSchedule.At = 112;
                    Assert.True(await mineCast);
                    var trapCast = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self, trapSource.Id,
                        (100, 100), (107, 107));
                    ownerSchedule.At = 114;
                    Assert.True(await trapCast);
                    var minePlaced = Assert.Single(self.Placed.All.Values, placed => placed.Skill == mineSource.Id);
                    var trapPlaced = Assert.Single(self.Placed.All.Values, placed => placed.Skill == trapSource.Id);
                    uint placedReserved = (uint)global::Game.ReservedMana(self);
                    Task removeMine;
                    var mineGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(mineGate.Wait(0));
                    try
                    {
                        removeMine = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                            Target(PlayerInventories.BodyArmour, bodyHost.Id, 3));
                        Assert.False(removeMine.IsCompleted);
                        Assert.DoesNotContain(minePlaced.Id, self.Placed.All.Keys);
                        Assert.Contains(trapPlaced.Id, self.Placed.All.Keys);
                        Assert.True(global::Game.ReservedMana(self) < placedReserved);
                        var cleanup = Assert.Single(self.Minions.OutputQueue,
                            output => output.Log == "placed skill source removed");
                        Assert.Equal(InstanceProtocol.RemoveObject(minePlaced.Id), cleanup.Packet);
                    }
                    finally { mineGate.Release(); }
                    await removeMine;
                    await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self,
                        Target(PlayerInventories.BodyArmour, bodyHost.Id, 3));
                    Assert.DoesNotContain(self.Placed.All.Values, placed => placed.Skill == mineSource.Id);

                    IReadOnlyList<PlacedSkills.Placed> PlacedFor(ushort skill) =>
                        [.. self.Placed.All.Values.Where(placed => placed.Skill == skill)];
                    bool HasPacket(IEnumerable<byte[]> packets, byte[] expected) =>
                        packets.Any(packet => packet.AsSpan().IndexOf(expected) >= 0);
                    bool HasCreate(IEnumerable<byte[]> packets, uint id) =>
                        packets.Any(packet => packet.Length >= 6
                            && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId
                            && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == id);
                    byte[] ExpectedMineCreate(ushort skill, uint id)
                    {
                        using var scope = new global::Game.PlayerStatScope(self, fresh: true);
                        var gem = global::Game.SupportedSkill(self, skill);
                        var player = global::Game.CharacterPlayerStats(self);
                        int Get(string name) => global::Program.Stats.Key(name) is uint key
                            ? gem.Stats.GetValueOrDefault(key) + player.GetValueOrDefault(key) : 0;
                        var payload = new Dictionary<uint, int>(gem.Stats);
                        foreach (string flag in new[] { "is_trap", "is_remote_mine", "base_skill_is_trapped",
                            "base_skill_is_mined" })
                            if (global::Program.Stats.Key(flag) is uint key) payload[key] = 0;
                        if (global::Program.Stats.Key("active_skill_area_of_effect_radius") is uint radius)
                            payload[radius] = (int)global::Game.SkillRadius(self, gem.Stats, true, gem.Effect);
                        if (global::Program.Stats.Key("active_skill_base_area_of_effect_radius") is uint baseRadius)
                            payload[baseRadius] = SkillArea.BaseRadius(
                                global::Game.SkillsFor(self).EffectId(gem.Effect), Get);
                        var proxy = global::Game.SkillsFor(self).FindByRow(0x4000, gem.Effect, gem.Level,
                            [.. payload.Select(pair => (pair.Key, pair.Value))]);
                        Assert.NotNull(proxy);
                        proxy = proxy! with { Parent = (Program.PlayerId, skill) };
                        var stats = new List<(uint, int)>();
                        foreach (var (name, value) in new[] { ("maximum_life", 1), ("uses_no_mana", 1),
                            ("is_trap_object", 0), ("is_remote_mine", 1) })
                            if (global::Program.Stats.Key(name) is uint key) stats.Add((key, value));
                        double flight = Get("trap_throw_arm_time_override") is > 0 and var armMs
                            ? armMs / 1000d : .6;
                        if (global::Program.Stats.Key("trap_throw_arm_time_override") is uint armTime)
                            stats.Add((armTime, (int)Math.Round(flight * 1000)));
                        return InstanceEntities.CreateObject(id,
                            InstanceEntities.ObjectTypeHash("Metadata/MiscellaneousObjects/RemoteMine"),
                            new TrapMineObject(100, 100, true, [proxy], stats).Blob());
                    }

                    // Actual paid output is suspended by its own one-shot observer. The observer starts the real
                    // physical removal and reinsertion and disarms before either handler can publish. Both handlers
                    // mutate synchronously before their first send wait, so the old cast resumes with a retired lease.
                    mineSource = Source("StormblastMine");
                    var paidMineLease = self.GemSourceLifetimes.Capture(mineSource.Identity);
                    var paidMineIds = self.Placed.All.Keys.ToHashSet();
                    uint paidMineObjectStart = self.NextObjectId;
                    var paidMinePackets = new List<byte[]>();
                    var previousPaidMineOutgoing = Program.Outgoing;
                    Task paidMineUnsocket = null!;
                    Task paidMineSocket = null!;
                    bool replacedAtPaidOutput = false;
                    void CollectPaidMine(ConnectionCiphers ciphers, byte[] bytes)
                    {
                        if (ReferenceEquals(ciphers, wire.Ciphers)) lock (paidMinePackets) paidMinePackets.Add(bytes.ToArray());
                        previousPaidMineOutgoing?.Invoke(ciphers, bytes);
                    }
                    Program.Outgoing = (ciphers, bytes) =>
                    {
                        CollectPaidMine(ciphers, bytes);
                        if (!ReferenceEquals(ciphers, wire.Ciphers) || bytes.Length < 2
                            || BinaryPrimitives.ReadUInt16BigEndian(bytes) != 0x01b1)
                            return;
                        lock (paidMinePackets)
                        {
                            if (replacedAtPaidOutput) return;
                            replacedAtPaidOutput = true;
                            Program.Outgoing = CollectPaidMine;
                            paidMineUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                                Target(PlayerInventories.BodyArmour, bodyHost.Id, 3));
                            paidMineSocket = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self,
                                Target(PlayerInventories.BodyArmour, bodyHost.Id, 3));
                        }
                    };
                    Task<bool> stalePaidMine = null!;
                    try
                    {
                        stalePaidMine = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                            mineSource.Id, (100, 100), (109, 109));
                        Assert.True(replacedAtPaidOutput,
                            "the actual paid mine output did not reach the one-shot physical source replacement");
                        Assert.False(await stalePaidMine);
                        await paidMineUnsocket;
                        await paidMineSocket;
                    }
                    finally { Program.Outgoing = previousPaidMineOutgoing; }
                    Assert.Equal(mineSource.Id, Source("StormblastMine").Id);
                    Assert.Null(self.GemSourceLifetimes.Current(mineSource.Identity));
                    Assert.NotSame(paidMineLease, self.GemSourceLifetimes.Current(mineSource.Identity));
                    Assert.Equal(paidMineObjectStart, self.NextObjectId);
                    Assert.True(paidMineIds.SetEquals(self.Placed.All.Keys));
                    Assert.DoesNotContain(paidMinePackets, packet => HasCreate([packet], paidMineObjectStart)
                        || packet.AsSpan().IndexOf(InstanceProtocol.EnterWakeZone(paidMineObjectStart)) >= 0);

                    // The same physical source now has a fresh continuous lifetime. A pending cast levels it through the
                    // real XP and level-up batch while the final send gate is held; the old cast must construct its proxy,
                    // damage and reservation from that one new profile rather than its level-one admission copy.
                    mineSource = Source("StormblastMine");
                    var mineGem = inventory.Items(PlayerInventories.BodyArmour).Single(item => item.Id == bodyHost.Id)
                        .Item.Sockets[3].Gem!;
                    var mineGemType = global::Game.ItemsFor(self).Gem(
                        global::Game.ItemsFor(self).ByHash(mineGem.TypeHash)!)!;
                    int mineLevelBefore = global::Game.ItemsFor(self).GemLevel(mineGem);
                    Assert.InRange(mineLevelBefore, 1, mineGemType.MaxLevel - 1);
                    Dictionary<uint, int> mineOwnBefore;
                    global::Game.SupportedGem mineSupportedBefore;
                    using (new global::Game.PlayerStatScope(self, fresh: true))
                    {
                        mineSupportedBefore = global::Game.SupportedSkill(self, mineSource.Id);
                        mineOwnBefore = new(mineSupportedBefore.Stats);
                    }
                    var mineXpSchedule = new HeldAfterFirstAreaWaitSchedule { At = 120 };
                    var mineXpUpdates = new PlayerUpdatesState(lifetime, mineXpSchedule);
                    var previousMineXpContext = global::Game.AreaUpdateContext.Value;
                    global::Game.AreaUpdateContext.Value = mineXpUpdates;
                    Task<bool> mineXpCast;
                    List<byte[]> mineXpPackets = [];
                    var previousMineXpOutgoing = Program.Outgoing;
                    global::GemSourceLifetimes.SourceLifetime? mineXpLease = null;
                    try
                    {
                        var currentMine = Source("StormblastMine");
                        uint mineXpObjectStart = self.NextObjectId;
                        Program.Outgoing = (ciphers, bytes) =>
                        {
                            if (ReferenceEquals(ciphers, wire.Ciphers)) lock (mineXpPackets) mineXpPackets.Add(bytes.ToArray());
                            previousMineXpOutgoing?.Invoke(ciphers, bytes);
                        };
                        mineXpCast = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                            currentMine.Id, (100, 100), (110, 110));
                        var first = await Task.WhenAny(mineXpCast, mineXpSchedule.Entered.Task);
                        Assert.True(ReferenceEquals(mineXpSchedule.Entered.Task, first),
                            "the actual mine XP cast completed or failed before its post-payment release wait");
                        mineXpLease = self.GemSourceLifetimes.Current(currentMine.Identity);
                        Assert.NotNull(mineXpLease);
                        var mineXpGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                        Assert.True(mineXpGate.Wait(0));
                        bool mineXpGateHeld = true;
                        try
                        {
                            mineXpSchedule.Resume();
                            Assert.False(mineXpCast.IsCompleted,
                                "the actual mine cast did not remain at its final send after release");
                            Assert.Equal(mineXpObjectStart, self.NextObjectId);
                            uint gain = checked((uint)((mineGemType.ExperienceFor(mineLevelBefore + 1)
                                - mineGem.GemExperience) * 10));
                            var (xpPackets, leveled) = global::Game.CommitGemExperience(self, gain);
                            Assert.False(leveled);
                            Assert.NotEmpty(global::Game.LevelUpReadyGems(self));
                            Assert.NotEmpty(xpPackets);
                            var leveledMineGem = inventory.Items(PlayerInventories.BodyArmour)
                                .Single(item => item.Id == bodyHost.Id).Item.Sockets[3].Gem!;
                            Assert.Equal(mineLevelBefore + 1, global::Game.ItemsFor(self).GemLevel(leveledMineGem));
                            Assert.Same(mineXpLease, self.GemSourceLifetimes.Current(currentMine.Identity));
                            mineXpGate.Release();
                            mineXpGateHeld = false;
                            Assert.True(await mineXpCast);
                            global::Game.SupportedGem mineSupportedAfter;
                            global::SkillCatalog.ReservationCost? mineReservationAfter;
                            (bool Life, int Efficiency) mineContextAfter;
                            DamagePacket expectedMineDamage;
                            byte[] expectedMineCreate;
                            var mineAfter = Assert.Single(PlacedFor(currentMine.Id),
                                placed => placed.Id >= mineXpObjectStart);
                            Assert.Equal(mineXpObjectStart, mineAfter.Id);
                            Assert.Same(mineXpLease, self.GemSourceLifetimes.Current(currentMine.Identity));
                            using (new global::Game.PlayerStatScope(self, fresh: true))
                            {
                                mineSupportedAfter = global::Game.SupportedSkill(self, currentMine.Id);
                                mineReservationAfter = global::Game.SupportedReservationCost(self, currentMine.Id);
                                mineContextAfter = global::Game.ReservationContext(self, currentMine.Id);
                                expectedMineDamage = global::Game.SupportedHitFor(self, currentMine.Id, suppress: false);
                            }
                            expectedMineCreate = ExpectedMineCreate(currentMine.Id, mineAfter.Id);
                            Assert.Equal(mineSupportedAfter.Level, mineLevelBefore + 1);
                            Assert.True(mineOwnBefore.Count == mineSupportedAfter.Stats.Count
                                && mineOwnBefore.Any(pair => mineSupportedAfter.Stats.GetValueOrDefault(pair.Key) != pair.Value));
                            Assert.True(mineAfter.Own.Count == mineSupportedAfter.Stats.Count
                                && mineAfter.Own.All(pair => mineSupportedAfter.Stats.TryGetValue(pair.Key, out int value)
                                    && value == pair.Value));
                            foreach (var type in DamageTypes.ConversionOrder)
                                Assert.Equal(expectedMineDamage[type], mineAfter.Damage[type]);
                            Assert.Equal(mineReservationAfter!.Value.ManaFlat, mineAfter.Flat);
                            Assert.Equal(mineReservationAfter.Value.ManaPercent, mineAfter.Percent);
                            Assert.Equal(mineContextAfter.Life, mineAfter.ReservesLife);
                            Assert.Equal(mineContextAfter.Efficiency, mineAfter.Efficiency);
                            Assert.True(HasPacket(mineXpPackets, expectedMineCreate));
                            Assert.True(HasPacket(mineXpPackets, InstanceProtocol.EnterWakeZone(mineAfter.Id)));
                            Assert.True(HasPacket(mineXpPackets, InstanceProtocol.LifeUpdate(Program.PlayerId,
                                self.PlayerLife, self.PlayerReservedLife)));
                            Assert.True(HasPacket(mineXpPackets, InstanceProtocol.LifeUpdate(Program.PlayerId,
                                self.PlayerMana, (uint)global::Game.ReservedMana(self), pool: global::Game.ManaPool)));
                            Assert.Empty(self.Summons.Fighters);
                        }
                        finally
                        {
                            if (mineXpGateHeld) mineXpGate.Release();
                            if (!mineXpCast.IsCompleted) await mineXpCast;
                        }
                    }
                    finally
                    {
                        Program.Outgoing = previousMineXpOutgoing;
                        global::Game.AreaUpdateContext.Value = previousMineXpContext;
                    }

                    // The same physical gem is removed and reinserted behind the FINAL placement send. The cast has
                    // already reached its at-gate mutation request, so a still-current numeric skill id cannot adopt
                    // the replacement lifetime or allocate a native object.
                    var abaSource = Source("StormblastMine");
                    var abaLease = self.GemSourceLifetimes.Current(abaSource.Identity);
                    Assert.NotNull(abaLease);
                    var abaSchedule = new HeldAfterFirstAreaWaitSchedule { At = 125 };
                    var abaUpdates = new PlayerUpdatesState(lifetime, abaSchedule);
                    var previousAbaContext = global::Game.AreaUpdateContext.Value;
                    global::Game.AreaUpdateContext.Value = abaUpdates;
                    var abaPackets = new List<byte[]>();
                    var previousAbaOutgoing = Program.Outgoing;
                    Program.Outgoing = (ciphers, bytes) =>
                    {
                        if (ReferenceEquals(ciphers, wire.Ciphers)) lock (abaPackets) abaPackets.Add(bytes.ToArray());
                        previousAbaOutgoing?.Invoke(ciphers, bytes);
                    };
                    uint abaObjectStart = self.NextObjectId;
                    Task<bool> staleAba = null!;
                    Task abaUnsocket = null!, abaSocket = null!;
                    bool abaGateHeld = false;
                    uint abaReservedAtGate = 0, abaReservedLifeAtGate = 0;
                    HashSet<uint> abaIdsAtGate = [];
                    try
                    {
                        staleAba = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                            abaSource.Id, (100, 100), (115, 115), actionToken: self.ActionCount);
                        var first = await Task.WhenAny(staleAba, abaSchedule.Entered.Task);
                        Assert.Same(abaSchedule.Entered.Task, first);
                        var abaGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                        Assert.True(abaGate.Wait(0));
                        abaGateHeld = true;
                        try
                        {
                            abaSchedule.Resume();
                            Assert.False(staleAba.IsCompleted,
                                "the actual mine cast did not remain at its final send after release");
                            Assert.Equal(abaObjectStart, self.NextObjectId);
                            abaUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                                Target(PlayerInventories.BodyArmour, bodyHost.Id, 3));
                            Assert.False(abaUnsocket.IsCompleted);
                            Assert.False(self.GemSourceLifetimes.IsCurrent(abaLease));
                            abaSocket = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self,
                                Target(PlayerInventories.BodyArmour, bodyHost.Id, 3));
                            Assert.False(abaSocket.IsCompleted);
                            var refreshedMine = Source("StormblastMine");
                            Assert.Equal(abaSource.Id, refreshedMine.Id);
                            Assert.True(abaLease.Retired || !self.GemSourceLifetimes.IsCurrent(abaLease));
                            Assert.True(refreshedMine.Active);
                            using (new global::Game.PlayerStatScope(self, fresh: true))
                                Assert.True(global::Game.SupportedSkill(self, refreshedMine.Id).RequirementsMet);
                            Assert.Null(self.GemSourceLifetimes.Current(refreshedMine.Identity));
                            Assert.True(global::Game.PlayerActionCurrent(self, self.ActionCount));
                            abaReservedAtGate = (uint)global::Game.ReservedMana(self);
                            abaReservedLifeAtGate = self.PlayerReservedLife;
                            abaIdsAtGate = self.Placed.All.Keys.ToHashSet();
                        }
                        finally
                        {
                            if (abaGateHeld)
                            {
                                abaGate.Release();
                                abaGateHeld = false;
                            }
                        }
                        Assert.False(await staleAba);
                        await abaUnsocket;
                        await abaSocket;
                        Assert.Equal(abaObjectStart, self.NextObjectId);
                        Assert.DoesNotContain(abaObjectStart, self.Placed.All.Keys);
                        Assert.False(HasCreate(abaPackets, abaObjectStart));
                        Assert.DoesNotContain(abaPackets,
                            packet => packet.AsSpan().IndexOf(InstanceProtocol.EnterWakeZone(abaObjectStart)) >= 0);
                        Assert.Equal(abaReservedAtGate, (uint)global::Game.ReservedMana(self));
                        Assert.Equal(abaReservedLifeAtGate, self.PlayerReservedLife);
                        Assert.True(abaIdsAtGate.SetEquals(self.Placed.All.Keys));
                    }
                    finally
                    {
                        if (abaGateHeld)
                            Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1)).Release();
                        abaSchedule.Resume();
                        if (staleAba is not null && !staleAba.IsCompleted) await staleAba;
                        if (abaUnsocket is not null) await abaUnsocket;
                        if (abaSocket is not null) await abaSocket;
                        Program.Outgoing = previousAbaOutgoing;
                        global::Game.AreaUpdateContext.Value = previousAbaContext;
                    }
                    var abaFreshSource = Source("StormblastMine");
                    ownerSchedule.At = 126;
                    Assert.True(await global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                        abaFreshSource.Id, (100, 100), (116, 116)));
                    var abaFreshLease = self.GemSourceLifetimes.Current(abaFreshSource.Identity);
                    Assert.NotNull(abaFreshLease);
                    Assert.True(self.GemSourceLifetimes.IsCurrent(abaFreshLease));
                    Assert.NotSame(abaLease, abaFreshLease);
                    Assert.True(PlacedFor(abaFreshSource.Id).MaxBy(placed => placed.Id)!.Mine);

                    // A different real support replaces the mine-conferring support while the cast waits at its final
                    // send. The original mine family must be refused; a fresh use is admitted as a trap.
                    var driftHost = inventory.Add(PlayerInventories.Offhand1, 0, 0,
                        ItemCatalog.Create(Program.Items.Find(
                            "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with
                        {
                            Sockets = [new(SocketColour.White, Gem("Fireball")),
                                new(SocketColour.White, Gem("Blastchain Mine Support"))],
                            Links = [2],
                        });
                    Recalculate();
                    var driftSource = Source("Fireball");
                    using (new global::Game.PlayerStatScope(self, fresh: true))
                        Assert.Equal((false, true), global::Game.PlacedKind(global::Game.SupportedSkill(self, driftSource.Id)));
                    var driftIds = self.Placed.All.Keys.ToHashSet();
                    uint driftObjectStart = self.NextObjectId;
                    var driftSchedule = new HeldAfterFirstAreaWaitSchedule { At = 130 };
                    var driftUpdates = new PlayerUpdatesState(lifetime, driftSchedule);
                    var previousDriftContext = global::Game.AreaUpdateContext.Value;
                    global::Game.AreaUpdateContext.Value = driftUpdates;
                    Task<bool>? staleDrift = null;
                    Task driftUnsocket = null!;
                    Task driftSocket = null!;
                    global::GemSourceLifetimes.SourceLifetime? driftLease = null;
                    bool driftGateHeld = false;
                    try
                    {
                        staleDrift = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                            driftSource.Id, (100, 100), (111, 111));
                        var first = await Task.WhenAny(staleDrift!, driftSchedule.Entered.Task);
                        Assert.Same(driftSchedule.Entered.Task, first);
                        driftLease = self.GemSourceLifetimes.Current(driftSource.Identity);
                        Assert.NotNull(driftLease);
                        var driftGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                        Assert.True(driftGate.Wait(0));
                        driftGateHeld = true;
                        try
                        {
                            driftSchedule.Resume();
                            Assert.False(staleDrift!.IsCompleted,
                                "the actual mine cast did not remain at its final send after release");
                            Assert.Equal(driftObjectStart, self.NextObjectId);
                            driftUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                                new InstanceItems.SocketGem(PlayerInventories.InventoryId(PlayerInventories.Offhand1),
                                    driftHost.Id, 1));
                            var blastchain = Assert.Single(inventory.Items(PlayerInventories.Cursor));
                            Assert.NotNull(inventory.Remove(PlayerInventories.Cursor, blastchain.Id));
                            inventory.Add(PlayerInventories.Cursor, 0, 0, Gem("Trap Support"));
                            driftSocket = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self,
                                new InstanceItems.SocketGem(PlayerInventories.InventoryId(PlayerInventories.Offhand1),
                                    driftHost.Id, 1));
                            Assert.Equal(driftSource.Id, Source("Fireball").Id);
                            using (new global::Game.PlayerStatScope(self, fresh: true))
                                Assert.Equal((true, false), global::Game.PlacedKind(global::Game.SupportedSkill(self,
                                    Source("Fireball").Id)));
                        }
                        finally
                        {
                            if (driftGateHeld)
                            {
                                driftGate.Release();
                                driftGateHeld = false;
                            }
                        }
                        Assert.False(await staleDrift!);
                        await driftUnsocket;
                        await driftSocket;
                    }
                    finally
                    {
                        if (driftGateHeld)
                            Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1)).Release();
                        driftSchedule.Resume();
                        if (staleDrift is not null && !staleDrift.IsCompleted) await staleDrift;
                        if (driftUnsocket is not null) await driftUnsocket;
                        if (driftSocket is not null) await driftSocket;
                        global::Game.AreaUpdateContext.Value = previousDriftContext;
                    }
                    Assert.Same(driftLease, self.GemSourceLifetimes.Current(driftSource.Identity));
                    Assert.Equal(driftObjectStart, self.NextObjectId);
                    Assert.True(driftIds.SetEquals(self.Placed.All.Keys));

                    driftSource = Source("Fireball");
                    var driftPackets = new List<byte[]>();
                    var previousDriftOutgoing = Program.Outgoing;
                    Program.Outgoing = (ciphers, bytes) =>
                    {
                        if (ReferenceEquals(ciphers, wire.Ciphers)) lock (driftPackets) driftPackets.Add(bytes.ToArray());
                        previousDriftOutgoing?.Invoke(ciphers, bytes);
                    };
                    try
                    {
                        ownerSchedule.At = 140;
                        Assert.True(await global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                            driftSource.Id, (100, 100), (112, 112)));
                    }
                    finally { Program.Outgoing = previousDriftOutgoing; }
                    var driftPlaced = Assert.Single(PlacedFor(driftSource.Id));
                    Assert.False(driftPlaced.Mine);
                    Assert.True(HasCreate(driftPackets, driftPlaced.Id));
                    Assert.True(HasPacket(driftPackets, InstanceProtocol.EnterWakeZone(driftPlaced.Id)));

                    // The inverse family transition is also fixed at admission. A real ordinary Fireball pending at
                    // its final gate must not become the newly inserted mine profile, while a later use may do so.
                    await global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.SocketGem(PlayerInventories.InventoryId(PlayerInventories.Offhand1),
                            driftHost.Id, 1));
                    var trapSupport = Assert.Single(inventory.Items(PlayerInventories.Cursor));
                    Assert.NotNull(inventory.Remove(PlayerInventories.Cursor, trapSupport.Id));
                    inventory.Add(PlayerInventories.Cursor, 0, 0, Gem("Blastchain Mine Support"));
                    uint ordinaryObjectStart = self.NextObjectId;
                    uint ordinaryManaBefore = self.PlayerMana, ordinaryLifeBefore = self.PlayerLife;
                    var ordinaryPackets = new List<byte[]>();
                    var previousOrdinaryOutgoing = Program.Outgoing;
                    Program.Outgoing = (ciphers, bytes) =>
                    {
                        if (ReferenceEquals(ciphers, wire.Ciphers)) lock (ordinaryPackets) ordinaryPackets.Add(bytes.ToArray());
                        previousOrdinaryOutgoing?.Invoke(ciphers, bytes);
                    };
                    Task<bool>? staleOrdinary = null;
                    Task? ordinaryGrace = null, ordinarySocket = null;
                    bool ordinaryGateHeld = false;
                    bool staleOrdinaryResult = true;
                    try
                    {
                        var ordinarySource = Source("Fireball");
                        var ordinaryLease = self.GemSourceLifetimes.Current(ordinarySource.Identity);
                        Assert.NotNull(ordinaryLease);
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            Assert.Equal((false, false), global::Game.PlacedKind(global::Game.SupportedSkill(self,
                                ordinarySource.Id)));
                        var ordinaryGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                        Assert.True(ordinaryGate.Wait(0));
                        ordinaryGateHeld = true;
                        ordinaryGrace = global::Game.StartGracePeriod("test", wire.Stream, wire.Ciphers, self);
                        Assert.False(ordinaryGrace.IsCompleted,
                            "the real grace-period begin packet did not suspend on the held send gate");
                        staleOrdinary = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                            ordinarySource.Id, (100, 100), (113, 113));
                        Assert.False(staleOrdinary!.IsCompleted,
                            "the ordinary cast did not suspend at EndGracePeriod's output");
                        ordinarySocket = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self,
                            new InstanceItems.SocketGem(PlayerInventories.InventoryId(PlayerInventories.Offhand1),
                                driftHost.Id, 1));
                        var ordinaryAfterSource = Source("Fireball");
                        Assert.Equal(ordinarySource.Id, ordinaryAfterSource.Id);
                        Assert.Same(ordinaryLease, self.GemSourceLifetimes.Current(ordinaryAfterSource.Identity));
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            Assert.Equal((false, true), global::Game.PlacedKind(global::Game.SupportedSkill(self,
                                ordinarySource.Id)));
                    }
                    finally
                    {
                        if (ordinaryGateHeld)
                            Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1)).Release();
                        if (ordinaryGrace is not null) await ordinaryGrace;
                        if (staleOrdinary is not null)
                        {
                            staleOrdinaryResult = await staleOrdinary!;
                        }
                        if (ordinarySocket is not null) await ordinarySocket;
                        Program.Outgoing = previousOrdinaryOutgoing;
                    }
                    Assert.False(staleOrdinaryResult);
                    Assert.Equal(ordinaryObjectStart, self.NextObjectId);
                    Assert.Equal(ordinaryManaBefore, self.PlayerMana);
                    Assert.Equal(ordinaryLifeBefore, self.PlayerLife);
                    Assert.DoesNotContain(ordinaryPackets, packet => packet.Length >= 6
                        && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId);
                    Assert.DoesNotContain(ordinaryPackets,
                        packet => packet.AsSpan().IndexOf(InstanceProtocol.EnterWakeZone(ordinaryObjectStart)) >= 0);
                    var freshOrdinarySource = Source("Fireball");
                    ownerSchedule.At = 146;
                    Assert.True(await global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                        freshOrdinarySource.Id, (100, 100), (114, 114)));
                    Assert.True(PlacedFor(freshOrdinarySource.Id).MaxBy(placed => placed.Id)!.Mine);

                    // An initially unqualified placed source is refused synchronously by admission capture. Making the
                    // same physical gem legal during the held grace output cannot turn that request into a later cast.
                    var amulet = Assert.Single(inventory.Items(amuletRow));
                    var removedAmulet = inventory.Remove(amuletRow, amulet.Id);
                    Assert.NotNull(removedAmulet);
                    Recalculate();
                    var unqualifiedSource = Source("StormblastMine");
                    using (new global::Game.PlayerStatScope(self, fresh: true))
                        Assert.False(global::Game.SupportedSkill(self, unqualifiedSource.Id).RequirementsMet);
                    uint unqualifiedObjectStart = self.NextObjectId;
                    uint unqualifiedManaBefore = self.PlayerMana, unqualifiedLifeBefore = self.PlayerLife;
                    var unqualifiedPackets = new List<byte[]>();
                    var previousUnqualifiedOutgoing = Program.Outgoing;
                    Program.Outgoing = (ciphers, bytes) =>
                    {
                        if (ReferenceEquals(ciphers, wire.Ciphers))
                            lock (unqualifiedPackets) unqualifiedPackets.Add(bytes.ToArray());
                        previousUnqualifiedOutgoing?.Invoke(ciphers, bytes);
                    };
                    Task<bool>? unqualifiedCast = null;
                    Task? unqualifiedGrace = null;
                    bool unqualifiedGateHeld = false;
                    bool amuletRestored = false;
                    try
                    {
                        var unqualifiedGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                        Assert.True(unqualifiedGate.Wait(0));
                        unqualifiedGateHeld = true;
                        unqualifiedGrace = global::Game.StartGracePeriod("test", wire.Stream, wire.Ciphers, self);
                        Assert.False(unqualifiedGrace.IsCompleted);
                        unqualifiedCast = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                            unqualifiedSource.Id, (100, 100), (117, 117));
                        Assert.True(unqualifiedCast!.IsCompleted,
                            "an initially unqualified placed source waited instead of refusing at admission capture");
                        Assert.False(await unqualifiedCast!);
                        Assert.Equal(unqualifiedObjectStart, self.NextObjectId);
                        Assert.Equal(unqualifiedManaBefore, self.PlayerMana);
                        Assert.Equal(unqualifiedLifeBefore, self.PlayerLife);
                        inventory.Add(amuletRow, 0, 0, removedAmulet!.Item);
                        Recalculate();
                        amuletRestored = true;
                        var legalAgain = Source("StormblastMine");
                        Assert.Equal(unqualifiedSource.Id, legalAgain.Id);
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            Assert.True(global::Game.SupportedSkill(self, legalAgain.Id).RequirementsMet);
                    }
                    finally
                    {
                        if (!amuletRestored)
                        {
                            inventory.Add(amuletRow, 0, 0, removedAmulet!.Item);
                            Recalculate();
                        }
                        if (unqualifiedGateHeld)
                            Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1)).Release();
                        if (unqualifiedGrace is not null) await unqualifiedGrace;
                        if (unqualifiedCast is not null) Assert.False(await unqualifiedCast);
                        Program.Outgoing = previousUnqualifiedOutgoing;
                    }
                    Assert.DoesNotContain(unqualifiedPackets, packet => packet.Length >= 6
                        && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId);
                    Assert.DoesNotContain(unqualifiedPackets,
                        packet => packet.AsSpan().IndexOf(InstanceProtocol.EnterWakeZone(unqualifiedObjectStart)) >= 0);
                    var legalSource = Source("StormblastMine");
                    ownerSchedule.At = 147;
                    Assert.True(await global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                        legalSource.Id, (100, 100), (118, 118)));
                    Assert.True(PlacedFor(legalSource.Id).MaxBy(placed => placed.Id)!.Mine);

                    // Supplementary controlled profile: real gem support still supplies the skill and family, while
                    // these player values exercise count-over-limit batching without manually seeding Placed.All.
                    var countKey = Program.Stats.Key("number_of_additional_mines_to_place")!.Value;
                    var limitKey = Program.Stats.Key("base_number_of_remote_mines_allowed")!.Value;
                    var additionalLimitKey =
                        Program.Stats.Key("number_of_additional_remote_mines_allowed")!.Value;
                    bool hadCount = self.PlayerStats.TryGetValue(countKey, out int previousCount);
                    bool hadLimit = self.PlayerStats.TryGetValue(limitKey, out int previousLimit);
                    bool hadAdditionalLimit =
                        self.PlayerStats.TryGetValue(additionalLimitKey, out int previousAdditionalLimit);
                    int baselineLimit;
                    using (new global::Game.PlayerStatScope(self, fresh: true))
                        baselineLimit = global::Game.PlacedSkillLimit(self,
                            global::Game.SupportedSkill(self, legalSource.Id), true);
                    Assert.True(baselineLimit > 1);
                    var countPackets = new List<byte[]>();
                    var previousCountOutgoing = Program.Outgoing;
                    Task<bool>? countedCast = null;
                    try
                    {
                        global::Game.SupportedGem countedGem;
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            countedGem = global::Game.SupportedSkill(self, legalSource.Id);
                        self.PlayerStats[countKey] = 1 - countedGem.Stats.GetValueOrDefault(countKey);
                        self.PlayerStats[limitKey] = 1 - countedGem.Stats.GetValueOrDefault(limitKey);
                        self.PlayerStats[additionalLimitKey] =
                            -countedGem.Stats.GetValueOrDefault(additionalLimitKey);
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                        {
                            countedGem = global::Game.SupportedSkill(self, legalSource.Id);
                            Assert.Equal(2, global::Game.PlacedSkillCount(self, countedGem, true));
                            Assert.Equal(1, global::Game.PlacedSkillLimit(self, countedGem, true));
                        }
                        uint firstId = self.NextObjectId;
                        Program.Outgoing = (ciphers, bytes) =>
                        {
                            if (ReferenceEquals(ciphers, wire.Ciphers))
                                lock (countPackets) countPackets.Add(bytes.ToArray());
                            previousCountOutgoing?.Invoke(ciphers, bytes);
                        };
                        ownerSchedule.At = 148;
                        countedCast = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                            legalSource.Id, (100, 100), (119, 119));
                        Assert.True(await countedCast!);
                        Assert.True(HasPacket(countPackets, ExpectedMineCreate(legalSource.Id, firstId)));
                        Assert.True(HasPacket(countPackets, ExpectedMineCreate(legalSource.Id, firstId + 1)));
                        Assert.DoesNotContain(firstId, self.Placed.All.Keys);
                        Assert.Contains(firstId + 1, self.Placed.All.Keys);
                        Assert.True(HasPacket(countPackets, InstanceProtocol.RemoveObject(firstId)));
                        Assert.DoesNotContain(countPackets,
                            packet => packet.AsSpan().IndexOf(InstanceProtocol.RemoveObject(firstId + 1)) >= 0);
                    }
                    finally
                    {
                        if (countedCast is not null && !countedCast.IsCompleted) await countedCast;
                        Program.Outgoing = previousCountOutgoing;
                        if (hadCount) self.PlayerStats[countKey] = previousCount;
                        else self.PlayerStats.Remove(countKey);
                        if (hadLimit) self.PlayerStats[limitKey] = previousLimit;
                        else self.PlayerStats.Remove(limitKey);
                        if (hadAdditionalLimit) self.PlayerStats[additionalLimitKey] = previousAdditionalLimit;
                        else self.PlayerStats.Remove(additionalLimitKey);
                    }

                    // Recovery, action, current work/output, current module lifetime and original instance are checked
                    // inside the held final send. Each refused mutation must leave the current owners/resources intact.
                    async Task AssertFinalControlRefused(ushort skill, double at, Action? mutate = null,
                        Action? restore = null, int? actionToken = null, bool outputGuard = false)
                    {
                        var beforeIds = self.Placed.All.Keys.ToHashSet();
                        var schedule = new HeldAfterFirstAreaWaitSchedule { At = at };
                        var updates = new PlayerUpdatesState(lifetime, schedule);
                        var previousContext = global::Game.AreaUpdateContext.Value;
                        global::Game.AreaUpdateContext.Value = updates;
                        var previousOutputGuard = Program.ProjectileOutputGuard.Value;
                        bool outputAllowed = true;
                        if (outputGuard) Program.ProjectileOutputGuard.Value = () => outputAllowed;
                        bool restored = false;
                        void RestoreOnce()
                        {
                            if (restored) return;
                            restored = true;
                            restore?.Invoke();
                        }
                        Task<bool>? cast = null;
                        bool gateHeld = false;
                        try
                        {
                            cast = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self, skill,
                                (100, 100), (113 + (int)at % 7, 113), actionToken: actionToken);
                            var first = await Task.WhenAny(cast!, schedule.Entered.Task);
                            Assert.Same(schedule.Entered.Task, first);
                            uint objectStart = self.NextObjectId;
                            var reserved = (uint)global::Game.ReservedMana(self);
                            uint reservedLife = self.PlayerReservedLife;
                            uint life = self.PlayerLife, mana = self.PlayerMana;
                            var gate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                            Assert.True(gate.Wait(0));
                            gateHeld = true;
                            try
                            {
                                schedule.Resume();
                                Assert.False(cast!.IsCompleted);
                                Assert.Equal(objectStart, self.NextObjectId);
                                if (outputGuard) outputAllowed = false;
                                else mutate?.Invoke();
                            }
                            finally
                            {
                                if (gateHeld)
                                {
                                    gate.Release();
                                    gateHeld = false;
                                }
                            }
                            Assert.False(await cast!);
                            RestoreOnce();
                            Assert.Equal(objectStart, self.NextObjectId);
                            Assert.True(beforeIds.SetEquals(self.Placed.All.Keys));
                            Assert.Equal(reserved, (uint)global::Game.ReservedMana(self));
                            Assert.Equal(reservedLife, self.PlayerReservedLife);
                            Assert.Equal(life, self.PlayerLife);
                            Assert.Equal(mana, self.PlayerMana);
                        }
                        finally
                        {
                            if (gateHeld)
                                Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1)).Release();
                            schedule.Resume();
                            if (cast is not null && !cast.IsCompleted) await cast;
                            RestoreOnce();
                            if (outputGuard) Program.ProjectileOutputGuard.Value = previousOutputGuard;
                            global::Game.AreaUpdateContext.Value = previousContext;
                        }
                    }

                    trapSource = Source("FireTrap");
                    // Controlled lifecycle fixture: this is the same computed ActionInterruptionGeneration a hostile
                    // interruption would advance, without claiming this mutation is an actual incoming hit.
                    await AssertFinalControlRefused(trapSource.Id, 150,
                        () => self.FreezeGeneration++);
                    await AssertFinalControlRefused(trapSource.Id, 151,
                        () => self.ActionCount++, actionToken: self.ActionCount);
                    await AssertFinalControlRefused(trapSource.Id, 152, outputGuard: true);
                    var previousRules = global::Game.CombatLifetime;
                    await AssertFinalControlRefused(trapSource.Id, 153,
                        () => global::Game.CombatLifetime = new ModuleLifetime("PlacedAdmissionOther"),
                        () => global::Game.CombatLifetime = previousRules);
                    // Controlled lifecycle fixture for the C# init-only property. This does not change the host API:
                    // the original instance is restored exactly once after the cast observes the mismatch.
                    var instanceProperty = typeof(ClientState).GetProperty(nameof(ClientState.Instance))!;
                    var previousInstance = self.Instance;
                    await AssertFinalControlRefused(trapSource.Id, 154,
                        () => instanceProperty.SetValue(self, new InstanceState()),
                        () => instanceProperty.SetValue(self, previousInstance));

                    // Existing-state fixture: seed one recent Fire use, then the actual final-gate refusal must not
                    // refresh it. The only principal action is the real Fire Trap cast below.
                    double retainedRecentAt = self.Clock.Elapsed.TotalSeconds;
                    self.RecentEvents.Clear();
                    self.RecentEvents.Record(StatConditions.UsedFireSkill, retainedRecentAt);
                    Assert.True(self.RecentEvents.Happened(StatConditions.UsedFireSkill, retainedRecentAt));
                    double? retainedExpiry = self.RecentEvents.NextExpiry(retainedRecentAt);
                    await AssertFinalControlRefused(trapSource.Id, 155, outputGuard: true);
                    Assert.False(self.RecentEvents.Happened(StatConditions.UsedFireSkill, retainedExpiry!.Value));
                    Assert.True(self.RecentEvents.Happened(StatConditions.UsedFireSkill, retainedRecentAt));
                    Assert.True(self.RecentEvents.Happened(StatConditions.SpentMana, retainedRecentAt));

                    self.RecentEvents.Clear();
                    uint acceptedTrapStart = self.NextObjectId;
                    ownerSchedule.At = 156;
                    Assert.True(await global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                        trapSource.Id, (100, 100), (121, 121)));
                    double acceptedRecentEnd = self.Clock.Elapsed.TotalSeconds;
                    Assert.True(self.RecentEvents.Happened(StatConditions.UsedFireSkill, acceptedRecentEnd));
                    Assert.False(self.RecentEvents.Happened(StatConditions.UsedFireSkill,
                        acceptedRecentEnd + RecentEvents.WindowSeconds));
                    var acceptedTrap = Assert.Single(PlacedFor(trapSource.Id), placed => placed.Id >= acceptedTrapStart);
                    Assert.False(acceptedTrap.Mine);

                    var flameSource = Source("SummonFlameGolem");
                    uint flameId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        flameSource.Id, global::Game.GolemData.Find("SummonFlameGolem")!, (100, 100), arrival: true);
                    Assert.NotEqual(0u, flameId);
                    var holySource = Source("SummonHolyRelic");
                    var holyDefinition = global::Game.SummonData.Find("SummonHolyRelic")!;
                    uint holyId = await global::Game.SummonFighter("test", wire.Stream, wire.Ciphers, self,
                        holySource.Id, holyDefinition, (100, 100));
                    Assert.NotEqual(0u, holyId);
                    var relic = self.Summons.Fighters[holyId];
                    var nova = Assert.Single(relic.Abilities, ability => ability.Effect is 9927 or 9928);
                    global::Game.HolyRelicRegenerationLocked(self, relic, nova, ownerSchedule.At);
                    Assert.NotEqual(0, self.Summons.HolyBuff);
                    ushort holyBuff = self.Summons.HolyBuff;
                    Task removeHoly;
                    var holyGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(holyGate.Wait(0));
                    try
                    {
                        removeHoly = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                            Target(PlayerInventories.Weapon1, weaponHost.Id, 1));
                        Assert.False(removeHoly.IsCompleted);
                        Assert.DoesNotContain(holyId, self.Summons.Fighters.Keys);
                        Assert.Contains(flameId, self.Golems.All.Keys);
                        Assert.Equal(0, self.Summons.HolyBuff);
                        Assert.DoesNotContain(Sources(), source => source.Id == holySource.Id);
                        Assert.DoesNotContain(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(
                            CharacterSaves.ModuleData(ownerCharacter, "Game.SummonedMinions") ?? "[]")!,
                            row => row.Source == holySource.Id);
                        Assert.Contains(self.Minions.OutputQueue,
                            output => output.Packet.AsSpan().SequenceEqual(
                                InstanceEntities.RemoveBuff(Program.PlayerId, holyBuff)));
                    }
                    finally { holyGate.Release(); }
                    await removeHoly;
                }
                finally
                {
                    global::Game.AreaUpdateContext.Value = previousOwnerContext;
                    if (owner is not null)
                    {
                        owner.Closed = true;
                        PlayerInventories.Forget(ownerCharacter);
                        global::Game.InactiveItems.TryRemove(ownerCharacter, out _);
                    }
                }
            }

            async Task PartialPlacementBudget()
            {
                // Real Witch level-100 attributes, catalogue item rolls and level-20 auras leave exactly eight flat
                // mana for a five-mine request. The production loop publishes only while each two-mana reservation
                // fits; this covers that flat-mana edge only, not Life/percent budgeting or an arbitrary build matrix.
                const string budgetCharacter = "PartialPlacementBudget";
                const byte witchRow = 1;
                const int expectedMana = 706, auraReserved = 698, mineReserve = 2;
                var budgetSchedule = new HeldAfterFirstAreaWaitSchedule { At = 230 };
                var budgetUpdates = new PlayerUpdatesState(lifetime, budgetSchedule);
                var previousBudgetContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = budgetUpdates;
                ClientState? budget = null;
                try
                {
                    var witch = Program.Skills.ClassFor(witchRow);
                    Assert.Equal(3, witch.IntegerId);
                    Assert.Equal((14, 14, 32), (witch.Strength, witch.Dexterity, witch.Intelligence));
                    var self = budget = new ClientState(
                        new(1, "partial-placement-budget", 1000, 1000, 100, 100, null, []), 10, 1)
                    {
                        Character = budgetCharacter, ClassRow = witchRow, Instance = new(),
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
                        PlayerUpdatesState = budgetUpdates,
                        Pools = new(100, 1, 1, 0, 0),
                        LeagueModule = conn.LeagueModule,
                    };
                    PlayerInventories.SetClass(budgetCharacter, witchRow);
                    self.Walk.Teleport(100, 100, budgetSchedule.At);
                    Assert.Empty(self.PlayerStats);

                    Item Gem(string name, int level)
                    {
                        var type = Program.Items.FindGem(name)!.Value;
                        var gemType = Program.Items.Gem(type.Type)!;
                        return ItemCatalog.Create(type.Type) with
                        {
                            GemVariant = type.Variant.Index,
                            GemExperience = gemType.ExperienceFor(level),
                        };
                    }
                    var inventory = global::Game.ItemsOf(self);
                    var bodyHost = inventory.Add(PlayerInventories.BodyArmour, 0, 0,
                        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!)
                        with
                        {
                            Sockets = [
                                new(SocketColour.White, Gem("Clarity", 20)),
                                new(SocketColour.White, Gem("Precision", 20)),
                                new(SocketColour.White, Gem("Vitality", 20)),
                            ],
                        });
                    var wandHost = inventory.Add(PlayerInventories.Weapon1, 0, 0,
                        ItemCatalog.Create(Program.Items.Find(
                            "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!)
                        with
                        {
                            Sockets = [
                                new(SocketColour.White, Gem("Stormblast Mine", 1)),
                                new(SocketColour.White, Gem("Minefield Support", 1)),
                            ],
                            Links = [2],
                        });

                    var amulet = new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!;
                    var amuletBase = Program.Items.Find("Metadata/Items/Amulets/Amulet7")!;
                    Assert.Equal(amuletBase.Hash, amulet.TypeHash);
                    ushort amuletVisual = amulet.VisualIdentity;
                    var originalAmuletMods = amulet.Mods.ToArray();
                    Item SetExistingRoll(Item item, string id, int value)
                    {
                        var mod = Program.Items.Mods.ById(id);
                        Assert.NotNull(mod);
                        Assert.Single(item.Mods, roll => roll.Hash == mod.Hash);
                        Assert.InRange(value, mod.RangeAt(0).Min, mod.RangeAt(0).Max);
                        return item with
                        {
                            Mods = [.. item.Mods.Select(roll => roll.Hash == mod.Hash
                                ? roll with { Values = [value] } : roll)],
                        };
                    }
                    amulet = SetExistingRoll(amulet, "AllAttributesImplicitAmulet1", 16);
                    amulet = SetExistingRoll(amulet, "AllAttributesUniqueAmulet8", 96);
                    Assert.Equal(amuletVisual, amulet.VisualIdentity);
                    Assert.Equal(originalAmuletMods.Length, amulet.Mods.Count);
                    Assert.Equal(16, amulet.Mods.Single(roll =>
                        Program.Items.Mods.ByHash(roll.Hash)?.Id == "AllAttributesImplicitAmulet1").Values.Single());
                    Assert.Equal(96, amulet.Mods.Single(roll =>
                        Program.Items.Mods.ByHash(roll.Hash)?.Id == "AllAttributesUniqueAmulet8").Values.Single());
                    inventory.Add(5, 0, 0, amulet);

                    var ringBase = Program.Items.Find("Metadata/Items/Rings/Ring1")!;
                    var ringImplicit = Assert.Single(ringBase.Implicits!);
                    var ring = ItemCrafter.Build(Program.Items, new ItemRolls(Program.Items), $$"""
                        {"base":"{{ringBase.Id}}","rarity":"rare","itemLevel":100,
                         "mods":[{"id":"{{ringImplicit.Id}}"},
                                 {"id":"Strength5","values":[30]},
                                 {"id":"Dexterity5","values":[30]}]}
                        """, 100, new Random(504), out string? ringError);
                    Assert.Null(ringError);
                    Assert.NotNull(ring);
                    Assert.Equal((ItemRarity.Rare, ringBase.Hash, 2),
                        (ring.Rarity, ring.TypeHash, ring.NameWords.Count));
                    Assert.Equal([ModSection.Implicit, ModSection.Explicit, ModSection.Explicit],
                        ring.Mods.Select(roll => roll.Section));
                    Assert.Equal([30, 30], ring.Mods.Skip(1).Select(roll => Assert.Single(roll.Values)));
                    inventory.Add(6, 0, 0, ring);

                    void Recalculate()
                    {
                        global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                        self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                        self.PlayerLife = self.Pools.Life;
                        self.PlayerMana = self.Pools.Mana;
                    }
                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        var equipment = inventory.EquipmentSnapshot();
                        return global::Game.PlayerSkillSourcesAt(self.Character, equipment,
                            global::Game.InactiveItems.GetValueOrDefault(self.Character), self.LeagueModule!,
                            [], global::Game.WornGrantedSkillsOf(self, equipment));
                    }
                    global::Game.PlayerSkillSource Source(string effect) => Assert.Single(
                        Sources(), source => global::Game.SkillsFor(self).EffectId(source.Effect) == effect);
                    void AssertAuraValues(AuraCatalog.Aura expected, AuraCatalog.Aura? actual)
                    {
                        Assert.NotNull(actual);
                        Assert.Equal(expected.SkillId, actual.SkillId);
                        Assert.Equal(expected.Effect, actual.Effect);
                        Assert.Equal(expected.EffectId, actual.EffectId);
                        Assert.Equal(expected.Name, actual.Name);
                        Assert.Equal(expected.Buff, actual.Buff);
                        Assert.Equal(expected.BuffRow, actual.BuffRow);
                        Assert.Equal(expected.SourceBuff, actual.SourceBuff);
                        Assert.Equal(expected.ManaFlat, actual.ManaFlat);
                        Assert.Equal(expected.ManaPercent, actual.ManaPercent);
                        Assert.Equal(expected.LifeFlat, actual.LifeFlat);
                        Assert.Equal(expected.LifePercent, actual.LifePercent);
                        Assert.Equal(expected.Level, actual.Level);
                        Assert.Equal(expected.Quality, actual.Quality);
                        Assert.Equal(expected.ReservesLife, actual.ReservesLife);
                        Assert.Equal(expected.AffectsSelf, actual.AffectsSelf);
                        Assert.Equal(expected.SupportEffectPct, actual.SupportEffectPct);
                        Assert.Equal(expected.ReservationEfficiencyPct, actual.ReservationEfficiencyPct);
                    }
                    Recalculate();
                    Assert.Equal((156, 156, 144, 100),
                        (self.Pools.Strength, self.Pools.Dexterity, self.Pools.Intelligence, self.Pools.Level));
                    Assert.Equal((uint)expectedMana, self.Pools.Mana);
                    Assert.Empty(self.PlayerStats);
                    Assert.All(inventory.EquipmentSnapshot(), equipped => Assert.DoesNotContain(
                        equipped.Item.Item.Mods, roll =>
                            Program.Items.Mods.ByHash(roll.Hash)?.Stats.Any(stat =>
                                stat.Id.Contains("mana", StringComparison.Ordinal)) == true));

                    var auraSources = new (string Name, int Reserve)[]
                    {
                        ("Clarity", 279), ("Precision", 186), ("Vitality", 233),
                    };
                    foreach (var (name, reserve) in auraSources)
                    {
                        var source = Source(name);
                        Assert.Equal(20, source.Level);
                        Assert.True(source.Active);
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                        {
                            var supported = global::Game.SupportedSkill(self, source.Id);
                            Assert.True(supported.RequirementsMet);
                            Assert.Null(global::Game.GemRequirementsUnmet(self, supported.SourceGem));
                        }
                        var cost = global::Game.SupportedReservationCost(self, source.Id);
                        Assert.NotNull(cost);
                        Assert.Equal((reserve, 0, 0, 0),
                            (cost!.Value.ManaFlat, cost.Value.ManaPercent,
                                cost.Value.LifeFlat, cost.Value.LifePercent));
                        Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                            source.Id, source.Effect, source.Name, cost.Value, source.Level, source.Quality));
                    }
                    var activeAuras = auraSources.Select(aura =>
                        Assert.Single(self.Auras.Values, current => current.EffectId == aura.Name)).ToArray();
                    string[] savedAuras = [.. CharacterSaves.Load(budgetCharacter)!.ActiveAuras!.Order()];
                    Assert.Equal(["Clarity", "Precision", "Vitality"], savedAuras);
                    Assert.Equal(auraReserved, global::Game.ReservedMana(self));
                    Assert.Equal(8, global::Game.UnreservedMana(self));
                    Assert.Equal(8u, self.PlayerMana);

                    var mineSource = Source("StormblastMine");
                    var mineSupported = global::Game.SupportedSkill(self, mineSource.Id);
                    var mineCost = global::Game.SupportedReservationCost(self, mineSource.Id);
                    Assert.NotNull(mineCost);
                    Assert.Equal((false, true), global::Game.PlacedKind(mineSupported));
                    Assert.Equal(5, global::Game.PlacedSkillCount(self, mineSupported, true));
                    Assert.Empty(self.Placed.All);
                    Assert.Equal(0, mineSupported.Stats.GetValueOrDefault(
                        Program.Stats.Key("base_number_of_remote_mines_allowed")!.Value));
                    Assert.Equal(3, mineSupported.Stats.GetValueOrDefault(
                        Program.Stats.Key("number_of_additional_remote_mines_allowed")!.Value));
                    Assert.Equal(18, global::Game.PlacedSkillLimit(self, mineSupported, true));
                    Assert.Equal((mineReserve, 0),
                        (mineCost!.Value.ManaFlat, mineCost.Value.ManaPercent));
                    Assert.Equal((false, 0), global::Game.ReservationContext(self, mineSource.Id));
                    Assert.Equal((mineReserve, 0),
                        global::Game.SupportedReservation(self, mineSource.Id)!.Value);
                    var minefield = Assert.Single(mineSupported.Supports);
                    Assert.Equal(140, minefield.CostMultiplier);
                    var manaKey = Program.Stats.Key("mana_cost")!.Value;
                    Assert.Equal(2u, global::Game.ManaCost(Program.Stats, Program.Skills,
                        mineSupported.Effect, mineSupported.Level, mineSupported.Quality));
                    using (new global::Game.PlayerStatScope(self, fresh: true))
                        Assert.Equal(2, global::Game.SupportedStats(self, mineSource.Id).GetValueOrDefault(manaKey));

                    await global::Game.StartGracePeriod("test", wire.Stream, wire.Ciphers, self);
                    double baselineTime = global::Game.ResourceEffectsTime(self);
                    long spentBefore = self.RecentEvents.Amounts(baselineTime)
                        .GetValueOrDefault(StatConditions.SpentMana);
                    uint lifeBeforeMines = self.PlayerLife;
                    uint objectStart = self.NextObjectId;
                    var minePackets = new List<byte[]>();
                    var previousMineOutgoing = Program.Outgoing;
                    Program.Outgoing = (ciphers, bytes) =>
                    {
                        if (ReferenceEquals(ciphers, wire.Ciphers))
                            lock (minePackets) minePackets.Add(bytes.ToArray());
                        previousMineOutgoing?.Invoke(ciphers, bytes);
                    };
                    Task<bool> budgetCast = null!;
                    bool mineGateHeld = false;
                    try
                    {
                        budgetCast = global::Game.CastGemSkill("test", wire.Stream, wire.Ciphers, self,
                            mineSource.Id, (100, 100), (110, 110));
                        var first = await Task.WhenAny(budgetCast, budgetSchedule.Entered.Task);
                        Assert.Same(budgetSchedule.Entered.Task, first);
                        var mineGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                        Assert.True(mineGate.Wait(0));
                        mineGateHeld = true;
                        try
                        {
                            budgetSchedule.Resume();
                            Assert.False(budgetCast.IsCompleted,
                                "the paid mine cast did not remain at its final send after release");
                            Assert.Equal(objectStart, self.NextObjectId);
                            Assert.Empty(self.Placed.All);
                            Assert.Equal(auraReserved, global::Game.ReservedMana(self));
                            Assert.Equal(6u, self.PlayerMana);
                            double paymentObservedTime = global::Game.ResourceEffectsTime(self);
                            Assert.True(paymentObservedTime >= baselineTime);
                            Assert.Equal(spentBefore + 2, self.RecentEvents.Amounts(paymentObservedTime)
                                .GetValueOrDefault(StatConditions.SpentMana));
                        }
                        finally
                        {
                            mineGate.Release();
                            mineGateHeld = false;
                        }
                        Assert.True(await budgetCast);
                    }
                    finally
                    {
                        if (mineGateHeld)
                            Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1)).Release();
                        budgetSchedule.Resume();
                        if (budgetCast is not null && !budgetCast.IsCompleted) await budgetCast;
                        Program.Outgoing = previousMineOutgoing;
                    }

                    var minePlaced = self.Placed.All.Values.OrderBy(placed => placed.Id).ToArray();
                    Assert.Equal(4, minePlaced.Length);
                    Assert.Equal([objectStart, objectStart + 1, objectStart + 2, objectStart + 3],
                        minePlaced.Select(placed => placed.Id));
                    Assert.Equal(objectStart + 5, self.NextObjectId);
                    Assert.All(minePlaced, placed =>
                    {
                        Assert.Equal(mineSource.Id, placed.Skill);
                        Assert.True(placed.Mine);
                        Assert.Equal((mineReserve, 0, false),
                            (placed.Flat, placed.Percent, placed.ReservesLife));
                    });
                    var mineLease = self.GemSourceLifetimes.Current(mineSource.Identity);
                    Assert.NotNull(mineLease);
                    Assert.Equal((uint)expectedMana, self.Pools.Mana);
                    Assert.Equal(expectedMana, global::Game.ReservedMana(self));
                    Assert.Equal(0, global::Game.UnreservedMana(self));
                    Assert.Equal(0u, self.PlayerMana);
                    Assert.Equal(lifeBeforeMines, self.PlayerLife);
                    double committedTime = global::Game.ResourceEffectsTime(self);
                    Assert.True(committedTime >= baselineTime);
                    Assert.Equal(spentBefore + 2, self.RecentEvents.Amounts(committedTime)
                        .GetValueOrDefault(StatConditions.SpentMana));

                    int CountSequence(IEnumerable<byte[]> packets, byte[] expected)
                    {
                        int count = 0;
                        foreach (var packet in packets)
                            for (int at = 0; at + expected.Length <= packet.Length; at++)
                                if (packet.AsSpan(at, expected.Length).SequenceEqual(expected)) count++;
                        return count;
                    }
                    void AssertAuraBuffPacketsRetained(IEnumerable<byte[]> packets)
                    {
                        foreach (var aura in activeAuras)
                        {
                            Assert.DoesNotContain(packets, packet =>
                                packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(Program.PlayerId, aura.Buff)) >= 0);
                            if (aura.SourceBuff is ushort sourceBuff)
                                Assert.DoesNotContain(packets, packet =>
                                    packet.AsSpan().IndexOf(InstanceEntities.RemoveBuff(
                                        Program.PlayerId, sourceBuff)) >= 0);
                        }
                    }
                    var remoteMineHash =
                        InstanceEntities.ObjectTypeHash("Metadata/MiscellaneousObjects/RemoteMine");
                    var created = new List<uint>();
                    foreach (var packet in minePackets)
                        for (int at = 0; at + 16 <= packet.Length; at++)
                            if (BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(at)) ==
                                    InstanceEntities.CreateObjectId
                                && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(at + 12)) ==
                                    remoteMineHash)
                                created.Add(BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(at + 2)));
                    Assert.Equal(minePlaced.Select(placed => placed.Id), created);
                    var profile = global::Game.SupportedSkill(self, mineSource.Id);
                    double flight;
                    using (new global::Game.PlayerStatScope(self, fresh: true))
                    {
                        var player = global::Game.CharacterPlayerStats(self);
                        int Get(string name) => Program.Stats.Key(name) is uint key
                            ? profile.Stats.GetValueOrDefault(key) + player.GetValueOrDefault(key) : 0;
                        flight = Get("trap_throw_arm_time_override") is > 0 and var armMs ? armMs / 1000d : .6;
                    }
                    var thrown = global::Game.SkillsFor(self).ActionSkillId("BeThrown");
                    Assert.NotNull(thrown);
                    foreach (var placed in minePlaced)
                    {
                        Assert.Equal(1, CountSequence(minePackets, InstanceProtocol.EnterWakeZone(placed.Id)));
                        Assert.Equal(1, CountSequence(minePackets, InstanceProtocol.ActionStarted(
                            placed.Id, 100, 100, 110, 110,
                            thrown.Value, 0xffff, 0x0408)));
                        Assert.Equal(1, CountSequence(minePackets, InstanceProtocol.MoveTo(placed.Id, 110, 110,
                            (float)flight, InstanceProtocol.FollowTerrain)));
                    }
                    uint rejectedId = objectStart + 4;
                    Assert.DoesNotContain(minePackets, packet => packet.AsSpan().IndexOf(
                        InstanceProtocol.RemoveObject(rejectedId)) >= 0);
                    foreach (var aura in activeAuras)
                    {
                        AssertAuraValues(aura, self.Auras.GetValueOrDefault(aura.SkillId));
                    }
                    AssertAuraBuffPacketsRetained(minePackets);
                    Assert.Equal(savedAuras, CharacterSaves.Load(budgetCharacter)!.ActiveAuras!.Order());

                    var cleanupPackets = new List<byte[]>();
                    var previousCleanupOutgoing = Program.Outgoing;
                    Program.Outgoing = (ciphers, bytes) =>
                    {
                        if (ReferenceEquals(ciphers, wire.Ciphers))
                            lock (cleanupPackets) cleanupPackets.Add(bytes.ToArray());
                        previousCleanupOutgoing?.Invoke(ciphers, bytes);
                    };
                    Task removeMineSource = null!;
                    bool cleanupGateHeld = false;
                    try
                    {
                        var cleanupGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                        Assert.True(cleanupGate.Wait(0));
                        cleanupGateHeld = true;
                        try
                        {
                            removeMineSource = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                                new InstanceItems.SocketGem(
                                    PlayerInventories.InventoryId(PlayerInventories.Weapon1), wandHost.Id, 0));
                            Assert.False(removeMineSource.IsCompleted);
                            Assert.Empty(self.Placed.All);
                            Assert.False(self.GemSourceLifetimes.IsCurrent(mineLease));
                            Assert.Null(self.GemSourceLifetimes.Current(mineSource.Identity));
                            Assert.Equal(auraReserved, global::Game.ReservedMana(self));
                            Assert.Equal(8, global::Game.UnreservedMana(self));
                            Assert.Equal(0u, self.PlayerMana);
                            Assert.Equal(lifeBeforeMines, self.PlayerLife);
                            var expectedCleanup = minePlaced.SelectMany(placed =>
                                InstanceProtocol.RemoveObject(placed.Id)).ToArray();
                            var cleanup = self.Minions.OutputQueue.Where(
                                output => output.Log == "placed skill source removed").ToArray();
                            Assert.Equal(minePlaced.Length, cleanup.Length);
                            Assert.All(cleanup, output => Assert.True(output.RemovesObject));
                            Assert.Equal(expectedCleanup, cleanup.SelectMany(output => output.Packet).ToArray());
                            foreach (var aura in activeAuras)
                                AssertAuraValues(aura, self.Auras.GetValueOrDefault(aura.SkillId));
                            Assert.Equal(savedAuras, CharacterSaves.Load(budgetCharacter)!.ActiveAuras!.Order());
                        }
                        finally
                        {
                            cleanupGate.Release();
                            cleanupGateHeld = false;
                        }
                        await removeMineSource;
                        await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                    }
                    finally
                    {
                        if (cleanupGateHeld)
                            Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1)).Release();
                        if (removeMineSource is not null && !removeMineSource.IsCompleted) await removeMineSource;
                        Program.Outgoing = previousCleanupOutgoing;
                    }
                    foreach (var placed in minePlaced)
                        Assert.Equal(1, CountSequence(cleanupPackets,
                            InstanceProtocol.RemoveObject(placed.Id)));
                    Assert.DoesNotContain(cleanupPackets, packet => packet.AsSpan().IndexOf(
                        InstanceProtocol.RemoveObject(rejectedId)) >= 0);
                    AssertAuraBuffPacketsRetained(cleanupPackets);

                    await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self,
                        new InstanceItems.SocketGem(
                            PlayerInventories.InventoryId(PlayerInventories.Weapon1), wandHost.Id, 0));
                    var reinserted = Source("StormblastMine");
                    Assert.True(reinserted.Active);
                    using (new global::Game.PlayerStatScope(self, fresh: true))
                        Assert.True(global::Game.SupportedSkill(self, reinserted.Id).RequirementsMet);
                    Assert.Empty(self.Placed.All);
                    Assert.Null(self.GemSourceLifetimes.Current(reinserted.Identity));
                    Assert.Equal(auraReserved, global::Game.ReservedMana(self));
                    Assert.Equal(8, global::Game.UnreservedMana(self));
                    Assert.Equal(0u, self.PlayerMana);
                    foreach (var aura in activeAuras)
                        AssertAuraValues(aura, self.Auras.GetValueOrDefault(aura.SkillId));
                    Assert.Equal(savedAuras, CharacterSaves.Load(budgetCharacter)!.ActiveAuras!.Order());
                }
                finally
                {
                    global::Game.AreaUpdateContext.Value = previousBudgetContext;
                    if (budget is not null)
                    {
                        budget.Closed = true;
                        PlayerInventories.Forget(budgetCharacter);
                        global::Game.InactiveItems.TryRemove(budgetCharacter, out _);
                    }
                }
            }

            async Task RighteousFireSourceOwnership()
            {
                const string rfCharacter = "RighteousFireSourceOwnership";
                var rfSchedule = new AreaClockSchedule { At = 140 };
                var rfUpdates = new PlayerUpdatesState(lifetime, rfSchedule);
                var previousRfContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = rfUpdates;
                ClientState? rfOwner = null;
                var emitted = new List<byte[]>();
                var previousOutgoing = Program.Outgoing;
                Program.Outgoing = (seen, bytes) =>
                {
                    if (ReferenceEquals(seen, wire.Ciphers)) lock (emitted) emitted.Add(bytes.ToArray());
                    previousOutgoing?.Invoke(seen, bytes);
                };
                try
                {
                    var self = rfOwner = new ClientState(
                        new(1, "righteous-fire-source-ownership", 1000, 1000, 100, 100, null, []), 12, 1)
                    {
                        Character = rfCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
                        PlayerUpdatesState = rfUpdates,
                        Pools = new(100, 10000, 10000, 2, 0, Strength: 65, Dexterity: 65, Intelligence: 65),
                        LeagueModule = conn.LeagueModule,
                    };
                    self.Golems.Restored = true;
                    self.Summons.Restored = true;
                    self.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                    self.Walk.Teleport(100, 100, rfSchedule.At);

                    Item Gem(string name)
                    {
                        var type = Program.Items.FindGem(name)!.Value;
                        return ItemCatalog.Create(type.Type) with { GemVariant = type.Variant.Index };
                    }
                    var inventory = global::Game.ItemsOf(self);
                    var wand = ItemCatalog.Create(
                        Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with
                    {
                        Sockets = [
                            new ItemSocket(SocketColour.White, Gem("Righteous Fire")),
                            new ItemSocket(SocketColour.White, Gem("Clarity")),
                            new ItemSocket(SocketColour.White, Gem("Summon Flame Golem")),
                        ],
                        Links = [1, 1, 1],
                    };
                    var host = inventory.Add(PlayerInventories.Weapon1, 0, 0, wand);
                    const byte amuletRow = 5;
                    inventory.Add(amuletRow, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    void Recalculate()
                    {
                        global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                        self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                        self.PlayerLife = self.Pools.Life;
                        self.PlayerMana = self.Pools.Mana;
                    }
                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        var equipment = inventory.EquipmentSnapshot();
                        return global::Game.PlayerSkillSourcesAt(self.Character, equipment,
                            global::Game.InactiveItems.GetValueOrDefault(self.Character), self.LeagueModule!,
                            [], global::Game.WornGrantedSkillsOf(self, equipment));
                    }
                    global::Game.PlayerSkillSource Source(string effect) => Assert.Single(
                        Sources(), source => global::Game.SkillsFor(self).EffectId(source.Effect) == effect);
                    InstanceItems.SocketGem Target(uint hostId, uint socket) =>
                        new(PlayerInventories.InventoryId(PlayerInventories.Weapon1), hostId, socket);
                    Task Activate(global::Game.PlayerSkillSource source) =>
                        global::Game.ActivateRighteousFire("test", wire.Stream, wire.Ciphers, self,
                            source.Id, source.Effect, source.Level, source.Quality, source.Gem);
                    bool HasPacket(IEnumerable<byte[]> packets, byte[] expected) =>
                        packets.Any(packet => packet.AsSpan().IndexOf(expected) >= 0);
                    bool HasBuffAdd(IEnumerable<byte[]> packets, ushort instance, ushort row, ushort skill)
                    {
                        byte[] prefix = InstanceEntities.AddBuff(Program.PlayerId, instance, row,
                            Program.PlayerId, skill).AsSpan(0, 22).ToArray();
                        return packets.Any(packet => packet.AsSpan().IndexOf(prefix) >= 0);
                    }
                    bool HasCreate(IEnumerable<byte[]> packets, uint id) =>
                        packets.Any(packet => packet.Length >= 6
                            && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId
                            && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == id);
                    void AssertCleanupQueued(RighteousFireState state, IReadOnlyList<(uint Id, ushort Buff)> enemyAuras)
                    {
                        var queue = self.Minions.OutputQueue.Select(output => output.Packet).ToArray();
                        Assert.True(HasPacket(queue, InstanceEntities.RemoveBuff(Program.PlayerId, state.Buff)));
                        Assert.True(HasPacket(queue, InstanceEntities.RemoveBuff(Program.PlayerId, state.SelfAura)));
                        if (state.Ring is uint ring)
                            Assert.True(HasPacket(queue, InstanceProtocol.RemoveObject(ring)));
                        foreach (var (id, buff) in enemyAuras)
                            Assert.True(HasPacket(queue, InstanceEntities.RemoveBuff(id, buff)));
                    }
                    Recalculate();
                    var rf = Source("RighteousFire");
                    Assert.True(rf.Active);
                    Assert.True(global::Game.SupportedSkill(self, rf.Id).RequirementsMet);
                    var clarity = Source("Clarity");
                    var clarityCost = global::Game.SupportedReservationCost(self, clarity.Id);
                    Assert.NotNull(clarityCost);
                    Assert.True(await global::Game.ToggleAura("test", wire.Stream, wire.Ciphers, self,
                        clarity.Id, clarity.Effect, clarity.Name, clarityCost!.Value, clarity.Level, clarity.Quality));
                    var golem = Source("SummonFlameGolem");
                    uint golemId = await global::Game.SummonGolem("test", wire.Stream, wire.Ciphers, self,
                        golem.Id, global::Game.GolemData.Find("SummonFlameGolem")!, (90, 90), arrival: true);
                    Assert.NotEqual(0u, golemId);

                    // The activation commits before its first native add, then the actual gem source retires while that
                    // first add is held. Reinsertion returns the same numeric skill id under a new lease, never old state.
                    var firstGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(firstGate.Wait(0));
                    Task oldActivation = null!, removeRf = null!, reinsertRf = null!, currentActivation = null!;
                    RighteousFireState oldState = null!;
                    var oldLease = self.GemSourceLifetimes.Current(rf.Identity);
                    try
                    {
                        oldActivation = Activate(rf);
                        Assert.False(oldActivation.IsCompleted);
                        oldState = Assert.IsType<RighteousFireState>(self.RighteousFire);
                        oldLease = oldState.SourceLifetime;
                        Assert.NotNull(oldLease);
                        removeRf = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self, Target(host.Id, 0));
                        Assert.False(removeRf.IsCompleted);
                        Assert.Null(self.RighteousFire);
                        Assert.False(self.GemSourceLifetimes.IsCurrent(oldLease));
                        AssertCleanupQueued(oldState, []);

                        reinsertRf = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self, Target(host.Id, 0));
                        Assert.False(reinsertRf.IsCompleted);
                        var replacement = Source("RighteousFire");
                        Assert.Equal(rf.Id, replacement.Id);
                        currentActivation = Activate(replacement);
                        Assert.False(currentActivation.IsCompleted);
                        var currentState = Assert.IsType<RighteousFireState>(self.RighteousFire);
                        Assert.Same(self.GemSourceLifetimes.Current(replacement.Identity), currentState.SourceLifetime);
                        Assert.NotSame(oldLease, currentState.SourceLifetime);
                    }
                    finally { firstGate.Release(); }
                    await oldActivation;
                    await removeRf;
                    await reinsertRf;
                    await currentActivation;
                    var currentRf = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    var currentLease = currentRf.SourceLifetime;
                    Assert.NotNull(currentLease);
                    Assert.Same(self.GemSourceLifetimes.Current(Source("RighteousFire").Identity), currentLease);
                    Assert.False(HasBuffAdd(emitted, oldState.Buff, global::Game.RighteousFireBuffRow,
                        oldState.Skill));
                    Assert.False(HasBuffAdd(emitted, oldState.SelfAura, global::Game.RighteousFireAuraBuffRow,
                        oldState.Skill));
                    if (oldState.Ring is uint oldRing)
                    {
                        Assert.False(HasCreate(emitted, oldRing));
                        Assert.False(HasPacket(emitted, InstanceProtocol.EnterWakeZone(oldRing)));
                    }
                    Assert.True(HasBuffAdd(emitted, currentRf.Buff, global::Game.RighteousFireBuffRow,
                        currentRf.Skill));
                    Assert.True(HasBuffAdd(emitted, currentRf.SelfAura, global::Game.RighteousFireAuraBuffRow,
                        currentRf.Skill));
                    if (currentRf.Ring is uint currentRing)
                    {
                        Assert.True(HasCreate(emitted, currentRing));
                        Assert.True(HasPacket(emitted, InstanceProtocol.EnterWakeZone(currentRing)));
                    }

                    // A real catalogue monster is admitted by the production spawn path. Current RF commits damage and
                    // its enemy aura; the next resource/player steps cannot resurrect that aura after source cleanup.
                    var variety = global::Game.Monsters.Find("Rhoa");
                    Assert.NotNull(variety);
                    rfSchedule.At = 141;
                    uint targetId = await global::Game.SpawnMonster("test", wire.Stream, wire.Ciphers, self,
                        variety!, 70, MonsterRarity.Normal, 102, 100, name: "Righteous Fire Rhoa");
                    Assert.NotEqual(0u, targetId);
                    var target = self.Monsters[targetId];
                    Assert.True(target.Hostile);
                    uint burningLife = target.Life;
                    await global::Game.BurnAround("test", wire.Stream, wire.Ciphers, self, currentRf, .25);
                    Assert.True(target.Life < burningLife);
                    Assert.True(currentRf.Auras.TryGetValue(targetId, out ushort enemyBuff));

                    var secondGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(secondGate.Wait(0));
                    Task secondUnsocket;
                    try
                    {
                        var enemyAuras = new[] { (targetId, enemyBuff) };
                        secondUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                            Target(host.Id, 0));
                        Assert.False(secondUnsocket.IsCompleted);
                        Assert.Null(self.RighteousFire);
                        Assert.False(self.GemSourceLifetimes.IsCurrent(currentLease));
                        AssertCleanupQueued(currentRf, enemyAuras);
                    }
                    finally { secondGate.Release(); }
                    await secondUnsocket;

                    uint staleLife = target.Life;
                    double staleBurn = target.Burn;
                    int staleQueue = self.Minions.OutputQueue.Count;
                    await global::Game.BurnAround("test", wire.Stream, wire.Ciphers, self, currentRf, .25);
                    Assert.Equal(staleLife, target.Life);
                    Assert.Equal(staleBurn, target.Burn);
                    Assert.Equal(staleQueue, self.Minions.OutputQueue.Count);
                    Assert.Empty(currentRf.Auras);

                    await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self, Target(host.Id, 0));
                    var replacementRf = Source("RighteousFire");
                    await Activate(replacementRf);
                    var replacementState = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    Assert.NotSame(currentRf, replacementState);
                    staleLife = target.Life;
                    staleBurn = target.Burn;
                    await global::Game.BurnAround("test", wire.Stream, wire.Ciphers, self, currentRf, .25);
                    Assert.Equal(staleLife, target.Life);
                    Assert.Equal(staleBurn, target.Burn);
                    await global::Game.BurnAround("test", wire.Stream, wire.Ciphers, self, replacementState, .25);
                    Assert.True(target.Life < staleLife);
                    Assert.True(replacementState.Auras.ContainsKey(targetId));

                    // An old gone-row snapshot carrying the reused numeric id cannot end this canonical current source.
                    await global::Game.EndSkillEffects("test", wire.Stream, wire.Ciphers, self, [replacementRf.Id]);
                    Assert.Same(replacementState, self.RighteousFire);
                    Assert.Same(replacementState.SourceLifetime,
                        self.GemSourceLifetimes.Current(replacementRf.Identity));

                    // Older captured ownership cannot end the replacement, even with the same numeric skill id.
                    var replacementLease = replacementState.SourceLifetime;
                    await global::Game.EndRighteousFire("test", wire.Stream, wire.Ciphers, self,
                        "older expected owner", currentRf);
                    Assert.Same(replacementState, self.RighteousFire);
                    Assert.Same(replacementLease, self.GemSourceLifetimes.Current(replacementRf.Identity));
                    Assert.Equal(replacementState.Buff, self.RighteousFire!.Buff);
                    uint lifeBeforeEnd = self.PlayerLife, manaBeforeEnd = self.PlayerMana;
                    int reservedBeforeEnd = global::Game.ReservedMana(self);
                    Assert.Contains(self.Auras.Values, aura => aura.EffectId == "Clarity");
                    Assert.Contains(golemId, self.Golems.All.Keys);
                    await global::Game.EndRighteousFire("test", wire.Stream, wire.Ciphers, self,
                        "current expected owner", replacementState);
                    Assert.Null(self.RighteousFire);
                    await global::Game.EndRighteousFire("test", wire.Stream, wire.Ciphers, self,
                        "already ended", replacementState);
                    Assert.Equal(lifeBeforeEnd, self.PlayerLife);
                    Assert.Equal(manaBeforeEnd, self.PlayerMana);
                    Assert.Equal(reservedBeforeEnd, global::Game.ReservedMana(self));
                    Assert.Contains(self.Auras.Values, aura => aura.EffectId == "Clarity");
                    Assert.Contains(golemId, self.Golems.All.Keys);

                    async Task RunRecoveryStep(Action resourceAt)
                    {
                        PlayerUpdatesState? updates = null;
                        updates = new PlayerUpdatesState(lifetime, new OneResourceStepSchedule(
                            self.Clock.Elapsed.TotalSeconds, resourceAt, () => updates!.Stop.Cancel()));
                        self.PlayerUpdatesState = updates;
                        Assert.True(self.PlayerUpdatesWorker.TryStart(self, lifetime, null, updates,
                            out long generation));
                        updates.Generation = generation;
                        try
                        {
                            await global::Game.RunResourceRecovery("test", wire.Stream, wire.Ciphers, self, updates);
                        }
                        finally
                        {
                            self.PlayerUpdatesWorker.Complete(lifetime, null, updates, generation);
                            self.RetirePlayerUpdates();
                        }
                    }

                    var resourceRf = Source("RighteousFire");
                    await Activate(resourceRf);
                    self.EnergyShield.SetMaximum(0, refill: true);
                    self.PlayerLife = global::Game.LifeCap(self);
                    uint positiveBefore = self.PlayerLife;
                    var positiveState = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    await RunRecoveryStep(() => { });
                    Assert.True(self.PlayerLife < positiveBefore);
                    Assert.True(self.PlayerLife > 1);
                    Assert.Same(positiveState, self.RighteousFire);

                    self.PlayerLife = global::Game.LifeCap(self);
                    uint sourceOffBefore = self.PlayerLife;
                    var sourceOffState = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    bool callbackSawCurrentState = false;
                    var resourceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(resourceGate.Wait(0));
                    Task sourceOff = null!, recovery = null!;
                    try
                    {
                        recovery = RunRecoveryStep(() =>
                        {
                            callbackSawCurrentState = ReferenceEquals(self.RighteousFire, sourceOffState);
                            sourceOff = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self,
                                Target(host.Id, 0));
                        });
                        Assert.Null(self.RighteousFire);
                        Assert.False(sourceOff.IsCompleted);
                    }
                    finally { resourceGate.Release(); }
                    await recovery;
                    await sourceOff;
                    Assert.True(callbackSawCurrentState,
                        "the resource boundary did not observe the captured RF before actual source removal");
                    Assert.NotSame(sourceOffState, self.RighteousFire);
                    Assert.True(self.PlayerLife >= sourceOffBefore,
                        $"an ended RF burned through the next resource boundary: {sourceOffBefore} -> {self.PlayerLife}");
                }
                finally
                {
                    Program.Outgoing = previousOutgoing;
                    global::Game.AreaUpdateContext.Value = previousRfContext;
                    if (rfOwner is not null)
                    {
                        rfOwner.Closed = true;
                        PlayerInventories.Forget(rfCharacter);
                        global::Game.InactiveItems.TryRemove(rfCharacter, out _);
                    }
                }
            }

            async Task RighteousFireSpellMoreProfile()
            {
                // Isolated ordinary spells and attacks beside a real RF gem. Raw gem experience is fixture preparation
                // only; every level below is crossed through normal XP or the public level-up request.
                const string character = "RighteousFireSpellMoreProfile";
                var schedule = new AreaClockSchedule { At = 160 };
                var updates = new PlayerUpdatesState(lifetime, schedule);
                var previousContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = updates;
                ClientState? owner = null;
                try
                {
                    var self = owner = new ClientState(
                        new(1, "righteous-fire-spell-more", 1000, 1000, 100, 100, null, []), 12, 1)
                    {
                        Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
                        PlayerUpdatesState = updates,
                        Pools = new(100, 10000, 10000, 2, 0, Strength: 65, Dexterity: 65, Intelligence: 65),
                        LeagueModule = conn.LeagueModule,
                    };
                    self.Walk.Teleport(100, 100, schedule.At);
                    const string moreName = "righteous_fire_spell_damage_+%_final";
                    uint moreKey = Program.Stats.Key(moreName)!.Value;
                    Item Gem(string name, int? rawExperience = null)
                    {
                        var found = Program.Items.FindGem(name)!.Value;
                        var gem = ItemCatalog.Create(found.Type) with { GemVariant = found.Variant.Index };
                        return rawExperience is int experience ? gem with { GemExperience = experience } : gem;
                    }
                    var rfBase = Program.Items.FindGem("Righteous Fire")!.Value;
                    var rfGemType = global::Game.ItemsFor(self).Gem(rfBase.Type)!;
                    var rfTemplate = ItemCatalog.Create(rfBase.Type) with { GemVariant = rfBase.Variant.Index };
                    int rfEffect = rfGemType.Variant(rfTemplate.GemVariant).Effect;
                    int SpellMoreAt(int level) => global::Game.SkillsFor(self)
                        .EffectStats(rfEffect, level).Where(stat => stat.Item1 == moreKey).Select(stat => stat.Item2)
                        .DefaultIfEmpty(0).Sum();
                    int crossing = Enumerable.Range(2, rfGemType.MaxLevel - 1)
                        .First(level => SpellMoreAt(level) > SpellMoreAt(level - 1));
                    Assert.InRange(crossing, 2, rfGemType.MaxLevel);

                    var inventory = global::Game.ItemsOf(self);
                    var wand = ItemCatalog.Create(
                        Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with
                    {
                        Sockets = [
                            new ItemSocket(SocketColour.White,
                                Gem("Righteous Fire", rfGemType.ExperienceFor(crossing) - 1)),
                            new ItemSocket(SocketColour.White, Gem("Fireball", 0)),
                            new ItemSocket(SocketColour.White, Gem("Power Siphon", 0)),
                        ],
                        Links = [1, 1, 1],
                    };
                    var host = inventory.Add(PlayerInventories.Weapon1, 0, 0, wand);
                    const byte amuletRow = 5;
                    inventory.Add(amuletRow, 0, 0,
                        new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    void Recalculate()
                    {
                        global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                        self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                        self.PlayerLife = self.Pools.Life;
                        self.PlayerMana = self.Pools.Mana;
                    }
                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        var equipment = inventory.EquipmentSnapshot();
                        return global::Game.PlayerSkillSourcesAt(self.Character, equipment,
                            global::Game.InactiveItems.GetValueOrDefault(self.Character), self.LeagueModule!,
                            [], global::Game.WornGrantedSkillsOf(self, equipment));
                    }
                    global::Game.PlayerSkillSource Source(string effect)
                    {
                        int effectRow = global::Game.SkillsFor(self).EffectRow(effect);
                        Assert.True(effectRow >= 0);
                        return Assert.Single(Sources(), source => source.Effect == effectRow);
                    }
                    Item Socketed(uint hostId, int socket) => inventory.Items(PlayerInventories.Weapon1)
                        .Single(item => item.Id == hostId).Item.Sockets[socket].Gem!;
                    int GemLevel(uint hostId, int socket) => global::Game.ItemsFor(self).GemLevel(Socketed(hostId, socket));
                    InstanceItems.SocketGem Target(uint hostId, uint socket) =>
                        new(PlayerInventories.InventoryId(PlayerInventories.Weapon1), hostId, socket);
                    Task Activate(global::Game.PlayerSkillSource source) =>
                        global::Game.ActivateRighteousFire("test", wire.Stream, wire.Ciphers, self,
                            source.Id, source.Effect, source.Level, source.Quality, source.Gem);
                    DamagePacket Hit(global::Game.PlayerSkillSource source) =>
                        global::Game.SupportedHitFor(self, source.Id, suppress: false);
                    int PlayerMore()
                    {
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            return global::Game.CharacterPlayerStats(self).GetValueOrDefault(moreKey);
                    }
                    int OwnMore(global::Game.PlayerSkillSource source)
                    {
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                            return global::Game.SupportedSkill(self, source.Id).Stats.GetValueOrDefault(moreKey);
                    }
                    static double MoreFactor(int more) => Math.Max(0, 1 + more / 100.0);
                    void AssertSpellMore(DamagePacket before, int beforeMore, DamagePacket after, int afterMore)
                    {
                        var expected = before.Scale(MoreFactor(afterMore) / MoreFactor(beforeMore));
                        Assert.Equal(expected.MinTotal, after.MinTotal, 9);
                        Assert.Equal(expected.MaxTotal, after.MaxTotal, 9);
                    }

                    Recalculate();
                    var rf = Source("RighteousFire");
                    var fireball = Source("Fireball");
                    var powerSiphon = Source("PowerSiphon");
                    Assert.False(global::Game.SkillsFor(self).IsAttack(fireball.Effect));
                    Assert.True(global::Game.SkillsFor(self).IsAttack(powerSiphon.Effect));
                    Assert.All(new[] { rf, fireball, powerSiphon },
                        source => Assert.True(global::Game.SupportedSkill(self, source.Id).RequirementsMet));
                    Assert.Equal(crossing - 1, rf.Level);
                    Assert.Equal(crossing - 1, GemLevel(host.Id, 0));
                    Assert.Equal(0, PlayerMore());

                    var fireballBefore = Hit(fireball);
                    var attackBefore = Hit(powerSiphon);
                    Assert.True(fireballBefore.MinTotal > 0);
                    Assert.True(attackBefore.MinTotal > 0);

                    // The accepted activation source captures its own current neutral value and publishes it through the
                    // ordinary player/stat and SupportedHitFor routes without direct RF or bonus setup.
                    var activationGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(activationGate.Wait(0));
                    Task? activation = null;
                    RighteousFireState activated = null!;
                    try
                    {
                        activation = Activate(rf);
                        Assert.False(activation.IsCompleted);
                        activated = Assert.IsType<RighteousFireState>(self.RighteousFire);
                        Assert.Equal(SpellMoreAt(crossing - 1), activated.SpellDamageMore);
                        Assert.Equal(activated.SpellDamageMore, OwnMore(rf));
                        Assert.Equal(activated.SpellDamageMore, PlayerMore());
                        var fireballAfter = Hit(fireball);
                        AssertSpellMore(fireballBefore, 0, fireballAfter, activated.SpellDamageMore);
                        var attackAfter = Hit(powerSiphon);
                        Assert.Equal(attackBefore.MinTotal, attackAfter.MinTotal, 9);
                        Assert.Equal(attackBefore.MaxTotal, attackAfter.MaxTotal, 9);
                    }
                    finally { activationGate.Release(); }
                    await activation!;
                    Assert.Same(activated, self.RighteousFire);

                    // An unrelated expired output guard is not authority over the current RF source or its projection.
                    var sourceLease = activated.SourceLifetime;
                    Assert.NotNull(sourceLease);
                    Assert.Same(sourceLease, self.GemSourceLifetimes.Current(rf.Identity));
                    int activationMore = activated.SpellDamageMore;
                    int outputCount = self.Minions.OutputQueue.Count;
                    var previousOutputGuard = Program.ProjectileOutputGuard.Value;
                    Program.ProjectileOutputGuard.Value = () => false;
                    try
                    {
                        Assert.False(global::Game.CurrentGameWork);
                        await global::Game.BurnAround("test", wire.Stream, wire.Ciphers, self, activated, .25);
                        Assert.Same(activated, self.RighteousFire);
                        Assert.Same(sourceLease, self.GemSourceLifetimes.Current(rf.Identity));
                        Assert.Equal(activationMore, activated.SpellDamageMore);
                        Assert.Equal(outputCount, self.Minions.OutputQueue.Count);
                        global::Game.RecomputePoolsAndSpeed(self);
                        Assert.Same(activated, self.RighteousFire);
                        Assert.Same(sourceLease, self.GemSourceLifetimes.Current(rf.Identity));
                        Assert.Equal(activationMore, activated.SpellDamageMore);
                        Assert.Equal(activationMore, PlayerMore());
                        AssertSpellMore(fireballBefore, 0, Hit(fireball), activationMore);
                    }
                    finally { Program.ProjectileOutputGuard.Value = previousOutputGuard; }

                    // Real normal XP fills the bar; the batch click crosses an adjacent real SpellMore increase. The held output
                    // proves the retained state, resources and spell hit advance before the first send resumes.
                    int oldMore = activated.SpellDamageMore;
                    int newMore = SpellMoreAt(crossing);
                    Assert.True(newMore > oldMore);
                    uint lifeBeforeGain = self.PlayerLife, manaBeforeGain = self.PlayerMana;
                    uint shieldBeforeGain = self.EnergyShield.Current;
                    var debtBeforeGain = self.PetrifiedBlood.Display(schedule.At);
                    var fireballAtOld = Hit(fireball);
                    var attackAtOld = Hit(powerSiphon);
                    var experienceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(experienceGate.Wait(0));
                    Task? normalExperience = null;
                    try
                    {
                        normalExperience = Task.WhenAll(global::Game.GainGemExperience("test", wire.Stream, wire.Ciphers, self, 10),
                            global::Game.LevelUpAllGems("test", wire.Stream, wire.Ciphers, self));
                        Assert.False(normalExperience.IsCompleted);
                        Assert.Same(activated, self.RighteousFire);
                        Assert.Same(sourceLease, self.GemSourceLifetimes.Current(rf.Identity));
                        Assert.Equal(activated.Buff, self.RighteousFire!.Buff);
                        Assert.Equal(activated.SelfAura, self.RighteousFire!.SelfAura);
                        Assert.Equal(activated.Ring, self.RighteousFire!.Ring);
                        Assert.Equal(crossing, GemLevel(host.Id, 0));
                        Assert.Equal(1, GemLevel(host.Id, 1));
                        Assert.Equal(1, GemLevel(host.Id, 2));
                        Assert.Equal(newMore, activated.SpellDamageMore);
                        Assert.Equal(newMore, OwnMore(rf));
                        Assert.Equal(newMore, PlayerMore());
                        AssertSpellMore(fireballAtOld, oldMore, Hit(fireball), newMore);
                        var attackAtNew = Hit(powerSiphon);
                        Assert.Equal(attackAtOld.MinTotal, attackAtNew.MinTotal, 9);
                        Assert.Equal(attackAtOld.MaxTotal, attackAtNew.MaxTotal, 9);
                        Assert.Equal(lifeBeforeGain, self.PlayerLife);
                        Assert.Equal(manaBeforeGain, self.PlayerMana);
                        Assert.Equal(shieldBeforeGain, self.EnergyShield.Current);
                        Assert.Equal(debtBeforeGain, self.PetrifiedBlood.Display(schedule.At));
                    }
                    finally { experienceGate.Release(); }
                    await normalExperience!;

                    // A real catalogue Rhoa gives an independent first-tick burn control. The next level changes only the
                    // neutral RF spell-more input; RF's own damage-over-time still must not consume that spell-hit factor.
                    schedule.At = 161;
                    var variety = global::Game.Monsters.Find("Rhoa");
                    Assert.NotNull(variety);
                    uint firstTargetId = await global::Game.SpawnMonster("test", wire.Stream, wire.Ciphers, self,
                        variety!, 70, MonsterRarity.Normal, 102, 100, name: "RF Spell More baseline Rhoa");
                    var firstTarget = self.Monsters[firstTargetId];
                    Assert.Equal(0, firstTarget.Burn);
                    uint firstTargetLife = firstTarget.Life;
                    await global::Game.BurnAround("test", wire.Stream, wire.Ciphers, self, activated, .25);
                    uint firstTickLoss = firstTargetLife - firstTarget.Life;
                    Assert.True(firstTickLoss > 0);

                    var oldLevelMore = activated.SpellDamageMore;
                    var fireballBeforeLevelUp = Hit(fireball);
                    var attackBeforeLevelUp = Hit(powerSiphon);
                    uint lifeBeforeLevelUp = self.PlayerLife, manaBeforeLevelUp = self.PlayerMana;
                    uint shieldBeforeLevelUp = self.EnergyShield.Current;
                    var debtBeforeLevelUp = self.PetrifiedBlood.Display(schedule.At);
                    var levelUpGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(levelUpGate.Wait(0));
                    Task? publicLevelUp = null;
                    int nextLevelMore = SpellMoreAt(crossing + 1);
                    try
                    {
                        publicLevelUp = global::Game.LevelUpGem(
                            "test", wire.Stream, wire.Ciphers, self, Target(host.Id, 0));
                        Assert.False(publicLevelUp.IsCompleted);
                        Assert.Same(activated, self.RighteousFire);
                        Assert.Same(sourceLease, self.GemSourceLifetimes.Current(rf.Identity));
                        Assert.Equal(activated.Buff, self.RighteousFire!.Buff);
                        Assert.Equal(activated.SelfAura, self.RighteousFire!.SelfAura);
                        Assert.Equal(activated.Ring, self.RighteousFire!.Ring);
                        Assert.Equal(crossing + 1, GemLevel(host.Id, 0));
                        Assert.Equal(nextLevelMore, activated.SpellDamageMore);
                        Assert.NotEqual(oldLevelMore, nextLevelMore);
                        Assert.Equal(nextLevelMore, OwnMore(rf));
                        Assert.Equal(nextLevelMore, PlayerMore());
                        AssertSpellMore(fireballBeforeLevelUp, oldLevelMore, Hit(fireball), nextLevelMore);
                        var attackAfterLevelUp = Hit(powerSiphon);
                        Assert.Equal(attackBeforeLevelUp.MinTotal, attackAfterLevelUp.MinTotal, 9);
                        Assert.Equal(attackBeforeLevelUp.MaxTotal, attackAfterLevelUp.MaxTotal, 9);
                        Assert.Equal(lifeBeforeLevelUp, self.PlayerLife);
                        Assert.Equal(manaBeforeLevelUp, self.PlayerMana);
                        Assert.Equal(shieldBeforeLevelUp, self.EnergyShield.Current);
                        Assert.Equal(debtBeforeLevelUp, self.PetrifiedBlood.Display(schedule.At));
                    }
                    finally { levelUpGate.Release(); }
                    await publicLevelUp!;

                    uint freshTargetId = await global::Game.SpawnMonster("test", wire.Stream, wire.Ciphers, self,
                        variety, 70, MonsterRarity.Normal, 104, 100, name: "RF Spell More fresh Rhoa");
                    var freshTarget = self.Monsters[freshTargetId];
                    Assert.Equal(firstTarget.MaxLife, freshTarget.MaxLife);
                    Assert.Equal(0, freshTarget.Burn);
                    uint freshTargetLife = freshTarget.Life;
                    await global::Game.BurnAround("test", wire.Stream, wire.Ciphers, self, activated, .25);
                    Assert.Equal(firstTickLoss, freshTargetLife - freshTarget.Life);

                    // Turning the physical RF source off clears the current owner and projection before the held output.
                    var fireballWithoutRf = fireballBefore;
                    var attackWithoutRf = attackBefore;
                    var removeGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(removeGate.Wait(0));
                    Task? remove = null;
                    try
                    {
                        remove = global::Game.UnsocketGem(
                            "test", wire.Stream, wire.Ciphers, self, Target(host.Id, 0));
                        Assert.False(remove.IsCompleted);
                        Assert.Null(self.RighteousFire);
                        Assert.False(self.GemSourceLifetimes.IsCurrent(sourceLease));
                        Assert.Equal(0, PlayerMore());
                        AssertSpellMore(fireballWithoutRf, 0, Hit(fireball), 0);
                        var attackOff = Hit(powerSiphon);
                        Assert.Equal(attackWithoutRf.MinTotal, attackOff.MinTotal, 9);
                        Assert.Equal(attackWithoutRf.MaxTotal, attackOff.MaxTotal, 9);
                    }
                    finally { removeGate.Release(); }
                    await remove!;

                    await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self, Target(host.Id, 0));
                    var restored = Source("RighteousFire");
                    Assert.True(restored.Active);
                    Assert.True(global::Game.SupportedSkill(self, restored.Id).RequirementsMet);
                    Assert.Null(self.RighteousFire);
                    Assert.Equal(0, PlayerMore());

                    await Activate(restored);
                    var reactivated = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    Assert.Equal(crossing + 1, restored.Level);
                    Assert.Equal(nextLevelMore, reactivated.SpellDamageMore);
                    Assert.Equal(nextLevelMore, PlayerMore());
                    AssertSpellMore(fireballWithoutRf, 0, Hit(fireball), nextLevelMore);
                }
                finally
                {
                    global::Game.AreaUpdateContext.Value = previousContext;
                    if (owner is not null)
                    {
                        owner.Closed = true;
                        PlayerInventories.Forget(character);
                        global::Game.InactiveItems.TryRemove(character, out _);
                    }
                }
            }

            async Task RighteousFireRadiusProfile()
            {
                // Real RF gem, real Astramentis and real normal-XP/public progression. Raw gem experience is setup only;
                // every radius change below is crossed by the production gem experience path.
                const string character = "RighteousFireRadiusProfile";
                var schedule = new AreaClockSchedule { At = 200 };
                var updates = new PlayerUpdatesState(lifetime, schedule);
                var previousContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = updates;
                ClientState? owner = null;
                var emitted = new List<byte[]>();
                var previousOutgoing = Program.Outgoing;
                Program.Outgoing = (seen, bytes) =>
                {
                    if (ReferenceEquals(seen, wire.Ciphers)) lock (emitted) emitted.Add(bytes.ToArray());
                    previousOutgoing?.Invoke(seen, bytes);
                };
                try
                {
                    var self = owner = new ClientState(
                        new(1, "righteous-fire-radius", 1000, 1000, 100, 100, null, []), 12, 1)
                    {
                        Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
                        PlayerUpdatesState = updates,
                        Pools = new(100, 10000, 10000, 2, 0, Strength: 65, Dexterity: 65, Intelligence: 65),
                        LeagueModule = conn.LeagueModule,
                    };
                    self.Walk.Teleport(100, 100, schedule.At);

                    Item Gem(string name, int experience)
                    {
                        var found = Program.Items.FindGem(name)!.Value;
                        return ItemCatalog.Create(found.Type) with
                        {
                            GemVariant = found.Variant.Index,
                            GemExperience = experience,
                        };
                    }
                    var rfBase = Program.Items.FindGem("Righteous Fire")!.Value;
                    var rfGemType = global::Game.ItemsFor(self).Gem(rfBase.Type)!;
                    var rfTemplate = ItemCatalog.Create(rfBase.Type) with { GemVariant = rfBase.Variant.Index };
                    int rfEffect = rfGemType.Variant(rfTemplate.GemVariant).Effect;
                    int RadiusAt(int level)
                    {
                        var own = global::Game.SkillsFor(self).EffectStats(rfEffect, level)
                            .ToDictionary(stat => stat.Item1, stat => stat.Item2);
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                        {
                            var merged = global::Game.MergeSkillStats(global::Game.AreaPlayerStats(self), own);
                            int Get(string name, int fallback) =>
                                Program.Stats.Key(name) is uint key && merged.TryGetValue(key, out int value)
                                    ? value : fallback;
                            return SkillArea.Radius(Get("active_skill_base_area_of_effect_radius", 18),
                                name => Get(name, 0), spell: true);
                        }
                    }
                    var radiusLevels = Enumerable.Range(2, rfGemType.MaxLevel - 1)
                        .Where(level => RadiusAt(level) > RadiusAt(level - 1)).ToList();
                    Assert.Equal(18, RadiusAt(1));
                    if (rfGemType.MaxLevel >= 20) Assert.Equal(23, RadiusAt(20));
                    Assert.True(radiusLevels.Count >= 4,
                        $"real Righteous Fire data has only {radiusLevels.Count} measured radius increases");

                    var inventory = global::Game.ItemsOf(self);
                    int initialExperience = radiusLevels[0] == 1 ? 0 : rfGemType.ExperienceFor(radiusLevels[0]) - 1;
                    var wand = ItemCatalog.Create(
                        Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with
                    {
                        Sockets = [new ItemSocket(SocketColour.White, Gem("Righteous Fire", initialExperience))],
                        Links = [1],
                    };
                    var host = inventory.Add(PlayerInventories.Weapon1, 0, 0, wand);
                    inventory.Add(5, 0, 0, new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                    self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                    // Existing-state control: legal partial pools and neutral debt are fixture state. AddDeferred is
                    // NOT a hostile-hit producer and does not activate Petrified Blood.
                    self.PlayerLife = Math.Max(1u, self.Pools.Life / 2);
                    self.PlayerMana = Math.Max(1u, self.Pools.Mana / 3);
                    self.PetrifiedBlood.AddDeferred(schedule.At, 40);
                    uint positiveLife = self.PlayerLife, positiveMana = self.PlayerMana;
                    var existingDebt = self.PetrifiedBlood.Display(schedule.At);
                    Assert.InRange(positiveLife, 1u, self.Pools.Life - 1);
                    Assert.InRange(positiveMana, 1u, self.Pools.Mana - 1);
                    Assert.True(existingDebt.PerMinute > 0);
                    Assert.True(existingDebt.Expiry > schedule.At);

                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        var equipment = inventory.EquipmentSnapshot();
                        return global::Game.PlayerSkillSourcesAt(self.Character, equipment,
                            global::Game.InactiveItems.GetValueOrDefault(self.Character), self.LeagueModule!,
                            [], global::Game.WornGrantedSkillsOf(self, equipment));
                    }
                    global::Game.PlayerSkillSource Source() => Assert.Single(
                        Sources(), source => global::Game.SkillsFor(self).EffectId(source.Effect) == "RighteousFire");
                    InstanceItems.SocketGem Target(uint socket) =>
                        new(PlayerInventories.InventoryId(PlayerInventories.Weapon1), host.Id, socket);
                    int CurrentGemLevel() => global::Game.ItemsFor(self).GemLevel(
                        inventory.Items(PlayerInventories.Weapon1).Single(item => item.Id == host.Id)
                            .Item.Sockets[0].Gem!);
                    int CurrentGemExperience() => inventory.Items(PlayerInventories.Weapon1)
                        .Single(item => item.Id == host.Id).Item.Sockets[0].Gem!.GemExperience;
                    uint GainToLevel(int level) =>
                        (uint)((rfGemType.ExperienceFor(level) - CurrentGemExperience()) * 10);
                    Task Activate(global::Game.PlayerSkillSource source) =>
                        global::Game.ActivateRighteousFire("test", wire.Stream, wire.Ciphers, self,
                            source.Id, source.Effect, source.Level, source.Quality, source.Gem);
                    int Mark()
                    {
                        lock (emitted) return emitted.Count;
                    }
                    List<byte[]> Since(int mark)
                    {
                        lock (emitted) return [.. emitted.Skip(mark)];
                    }
                    bool HasPacket(IEnumerable<byte[]> packets, byte[] expected) =>
                        packets.Any(packet => packet.AsSpan().IndexOf(expected) >= 0);
                    bool HasCreate(IEnumerable<byte[]> packets, uint id) =>
                        packets.Any(packet => packet.Length >= 6
                            && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId
                            && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == id);
                    bool HasBuffAdd(IEnumerable<byte[]> packets, ushort instance, ushort row, ushort skill)
                    {
                        byte[] prefix = InstanceEntities.AddBuff(Program.PlayerId, instance, row,
                            Program.PlayerId, skill).AsSpan(0, 22).ToArray();
                        return packets.Any(packet => packet.AsSpan().IndexOf(prefix) >= 0);
                    }
                    List<PlayerMinions.Output> RingOutputs(IEnumerable<PlayerMinions.Output> outputs,
                        RighteousFireState state, uint ring) => outputs.Where(output =>
                            output.Kind == PlayerMinions.OutputKind.RighteousFireRing
                            && ReferenceEquals(output.RighteousFireRing?.State, state)
                            && output.RighteousFireRing.Ring == ring).ToList();
                    var replies = new List<string>();
                    var commands = new ChatCommands
                    {
                        Send = (bytes, label) => Program.Send("test", wire.Stream, wire.Ciphers, bytes, label),
                        Reply = text => { replies.Add(text); return Task.CompletedTask; },
                    };
                    self.Commands = ConditionalSourceTestDrivers.Register(commands, "test", wire.Stream, wire.Ciphers, self);

                    // An actual activation is held after its owner commits but before any send. Source-off retires that
                    // exact owner; its pending buff and ring creates/wake never reach the wire.
                    Assert.Equal(radiusLevels[0] - 1, CurrentGemLevel());
                    var activationGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(activationGate.Wait(0));
                    Task? staleActivation = null, staleUnsocket = null;
                    RighteousFireState staleState = null!;
                    uint staleRing;
                    try
                    {
                        staleActivation = Activate(Source());
                        Assert.False(staleActivation.IsCompleted);
                        staleState = Assert.IsType<RighteousFireState>(self.RighteousFire);
                        staleRing = Assert.IsType<uint>(staleState.Ring);
                        staleUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self, Target(0));
                        Assert.False(staleUnsocket.IsCompleted);
                        Assert.Null(self.RighteousFire);
                    }
                    finally { activationGate.Release(); }
                    await staleActivation!;
                    await staleUnsocket!;
                    Assert.False(HasCreate(emitted, staleRing));
                    Assert.False(HasPacket(emitted, InstanceProtocol.EnterWakeZone(staleRing)));
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);

                    await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self, Target(0));
                    var source = Source();
                    int freshMark = Mark();
                    await Activate(source);
                    Assert.Equal(radiusLevels[0] - 1, CurrentGemLevel());
                    var live = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    uint liveRing = Assert.IsType<uint>(live.Ring);
                    var freshActivationPackets = Since(freshMark);
                    Assert.True(HasCreate(freshActivationPackets, liveRing));
                    Assert.True(HasPacket(freshActivationPackets, InstanceProtocol.EnterWakeZone(liveRing)));

                    int oldRadius = live.Radius;
                    int newRadius = RadiusAt(radiusLevels[0]);
                    Assert.True(newRadius > oldRadius);
                    var variety = global::Game.Monsters.Find("Rhoa");
                    Assert.NotNull(variety);
                    int stopRange = Math.Max(2, variety!.ObjectSize + 1);
                    int targetX = 100 + newRadius + stopRange;
                    uint targetId = await global::Game.SpawnMonster("test", wire.Stream, wire.Ciphers, self,
                        variety, 70, MonsterRarity.Normal, targetX, 100, name: "RF radius boundary Rhoa");
                    var target = self.Monsters[targetId];
                    double targetDistance = global::Game.Dist((100, 100), target.Walk.Position(schedule.At));
                    Assert.True(targetDistance > oldRadius + target.StopRange);
                    Assert.True(targetDistance <= newRadius + target.StopRange);
                    uint targetLife = target.Life;
                    double targetBurn = target.Burn;
                    await global::Game.BurnAround("test", wire.Stream, wire.Ciphers, self, live, .25);
                    Assert.Equal(targetLife, target.Life);
                    Assert.Equal(targetBurn, target.Burn);
                    Assert.DoesNotContain(targetId, live.Auras.Keys);

                    var sourceLease = live.SourceLifetime;
                    Assert.NotNull(sourceLease);
                    uint lifeBefore = self.PlayerLife, manaBefore = self.PlayerMana;
                    uint shieldBefore = self.EnergyShield.Current;
                    var debtBefore = self.PetrifiedBlood.Display(schedule.At);
                    Assert.Equal(positiveLife, lifeBefore);
                    Assert.Equal(positiveMana, manaBefore);
                    Assert.Equal(existingDebt, debtBefore);
                    Assert.Equal(radiusLevels[0] - 1, CurrentGemLevel());
                    var xpGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(xpGate.Wait(0));
                    Task? normalExperience = null;
                    try
                    {
                        normalExperience = Task.WhenAll(global::Game.GainGemExperience("test", wire.Stream, wire.Ciphers, self, 10),
                            global::Game.LevelUpAllGems("test", wire.Stream, wire.Ciphers, self));
                        Assert.False(normalExperience.IsCompleted);
                        Assert.Same(live, self.RighteousFire);
                        Assert.Same(sourceLease, self.GemSourceLifetimes.Current(source.Identity));
                        Assert.Equal(newRadius, live.Radius);
                        Assert.Equal(radiusLevels[0], CurrentGemLevel());
                        Assert.NotEqual(liveRing, Assert.IsType<uint>(live.Ring));
                        uint pendingRing = Assert.IsType<uint>(live.Ring);
                        Assert.Equal(lifeBefore, self.PlayerLife);
                        Assert.Equal(manaBefore, self.PlayerMana);
                        Assert.Equal(shieldBefore, self.EnergyShield.Current);
                        Assert.Equal(debtBefore, self.PetrifiedBlood.Display(schedule.At));
                        var pending = RingOutputs(self.Minions.OutputQueue, live, pendingRing);
                        Assert.Contains(pending, output => HasCreate([output.Packet], pendingRing));
                        Assert.Contains(pending, output => output.Packet.SequenceEqual(
                            InstanceProtocol.EnterWakeZone(pendingRing)));
                        Assert.Contains(self.Minions.OutputQueue, output => output.Packet.SequenceEqual(
                            InstanceProtocol.RemoveObject(liveRing)));
                    }
                    finally { xpGate.Release(); }
                    await normalExperience!;

                    int ringMark = Mark();
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                    var ringPackets = Since(ringMark);
                    var newRing = Assert.IsType<uint>(live.Ring);
                    Assert.True(HasPacket(ringPackets, InstanceProtocol.RemoveObject(liveRing)));
                    Assert.True(HasCreate(ringPackets, newRing));
                    Assert.True(HasPacket(ringPackets, InstanceProtocol.EnterWakeZone(newRing)));
                    Assert.False(HasBuffAdd(ringPackets, live.Buff, global::Game.RighteousFireBuffRow, live.Skill));
                    Assert.False(HasBuffAdd(ringPackets, live.SelfAura, global::Game.RighteousFireAuraBuffRow, live.Skill));
                    Assert.Same(live, self.RighteousFire);
                    Assert.Same(sourceLease, self.GemSourceLifetimes.Current(source.Identity));
                    Assert.Equal(lifeBefore, self.PlayerLife);
                    Assert.Equal(manaBefore, self.PlayerMana);
                    Assert.Equal(shieldBefore, self.EnergyShield.Current);
                    Assert.Equal(debtBefore, self.PetrifiedBlood.Display(schedule.At));
                    await global::Game.BurnAround("test", wire.Stream, wire.Ciphers, self, live, .25);
                    Assert.True(target.Life < targetLife);
                    Assert.Contains(targetId, live.Auras.Keys);

                    // Same level/profile means no ring identity or queued presentation change.
                    int queueBefore = self.Minions.OutputQueue.Count;
                    uint stableRing = Assert.IsType<uint>(live.Ring);
                    int stableRadius = live.Radius;
                    global::Game.RecomputePoolsAndSpeed(self);
                    Assert.Equal(stableRing, live.Ring);
                    Assert.Equal(stableRadius, live.Radius);
                    Assert.Equal(queueBefore, self.Minions.OutputQueue.Count);

                    // A held actual activation commits the original ring, then real progression crosses a radius step.
                    // The activation's immutable original create/wake are stale; only the retained owner's new ring goes.
                    await global::Game.EndRighteousFire("test", wire.Stream, wire.Ciphers, self,
                        "radius profile fixture reset", live);
                    while (CurrentGemLevel() < radiusLevels[1] - 1)
                    {
                        int nextLevel = CurrentGemLevel() + 1;
                        await global::Game.LevelUpGem("test", wire.Stream, wire.Ciphers, self, Target(0));
                        Assert.Equal(nextLevel, CurrentGemLevel());
                    }
                    Assert.Equal(radiusLevels[1] - 1, CurrentGemLevel());
                    var secondSource = Source();
                    var activationProgressGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(activationProgressGate.Wait(0));
                    Task? heldActivation = null, heldProgression = null;
                    RighteousFireState retained = null!;
                    uint retainedStartingRing;
                    ringMark = Mark();
                    try
                    {
                        heldActivation = Activate(secondSource);
                        Assert.Equal(radiusLevels[1] - 1, CurrentGemLevel());
                        retained = Assert.IsType<RighteousFireState>(self.RighteousFire);
                        retainedStartingRing = Assert.IsType<uint>(retained.Ring);
                        int retainedRadius = retained.Radius;
                        heldProgression = global::Game.LevelUpGem(
                            "test", wire.Stream, wire.Ciphers, self, Target(0));
                        Assert.False(heldProgression.IsCompleted);
                        Assert.Equal(radiusLevels[1], CurrentGemLevel());
                        Assert.Same(retained, self.RighteousFire);
                        Assert.Same(retained.SourceLifetime, self.GemSourceLifetimes.Current(secondSource.Identity));
                        Assert.Equal(RadiusAt(radiusLevels[1]), retained.Radius);
                        Assert.True(retained.Radius > retainedRadius);
                        uint retainedNewRing = Assert.IsType<uint>(retained.Ring);
                        Assert.NotEqual(retainedStartingRing, retainedNewRing);
                        Assert.Contains(RingOutputs(self.Minions.OutputQueue, retained, retainedNewRing),
                            output => HasCreate([output.Packet], retainedNewRing));
                    }
                    finally { activationProgressGate.Release(); }
                    await heldActivation!;
                    await heldProgression!;
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                    ringPackets = Since(ringMark);
                    Assert.False(HasCreate(ringPackets, retainedStartingRing));
                    Assert.False(HasPacket(ringPackets, InstanceProtocol.EnterWakeZone(retainedStartingRing)));
                    uint retainedFinalRing = Assert.IsType<uint>(retained.Ring);
                    Assert.Equal(1, ringPackets.Count(packet => HasCreate([packet], retainedFinalRing)));
                    Assert.Equal(1, ringPackets.Count(packet => packet.SequenceEqual(
                        InstanceProtocol.EnterWakeZone(retainedFinalRing))));

                    Task GainAndClickThrough(int targetLevel)
                    {
                        var outputs = new List<Task>();
                        while (CurrentGemLevel() < targetLevel)
                        {
                            int before = CurrentGemLevel();
                            outputs.Add(global::Game.GainGemExperience("test", wire.Stream, wire.Ciphers, self,
                                GainToLevel(targetLevel)));
                            outputs.Add(global::Game.LevelUpAllGems("test", wire.Stream, wire.Ciphers, self));
                            Assert.True(CurrentGemLevel() > before);
                        }
                        return Task.WhenAll(outputs);
                    }

                    // Two further real profile crossings queue intermediate and final rings before the minion drain. The
                    // exact receipt gate suppresses every intermediate create/wake and emits only the current ring.
                    Assert.Equal(radiusLevels[1], CurrentGemLevel());
                    var repeatedGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(repeatedGate.Wait(0));
                    Task? firstChange = null, finalChange = null;
                    try
                    {
                        firstChange = GainAndClickThrough(radiusLevels[2]);
                        Assert.False(firstChange.IsCompleted);
                        Assert.Equal(radiusLevels[2], CurrentGemLevel());
                        uint intermediateRing = Assert.IsType<uint>(retained.Ring);
                        Assert.Same(retained, self.RighteousFire);
                        finalChange = GainAndClickThrough(radiusLevels[3]);
                        Assert.False(finalChange.IsCompleted);
                        Assert.Equal(radiusLevels[3], CurrentGemLevel());
                        Assert.Same(retained, self.RighteousFire);
                        uint finalRing = Assert.IsType<uint>(retained.Ring);
                        Assert.NotEqual(intermediateRing, finalRing);
                        Assert.Equal(RadiusAt(radiusLevels[3]), retained.Radius);
                        var pending = self.Minions.OutputQueue.Where(output =>
                            output.Kind == PlayerMinions.OutputKind.RighteousFireRing).ToList();
                        Assert.Contains(pending, output => ReferenceEquals(output.RighteousFireRing?.State, retained)
                            && output.RighteousFireRing?.Ring == intermediateRing
                            && HasCreate([output.Packet], intermediateRing));
                        Assert.Contains(pending, output => ReferenceEquals(output.RighteousFireRing?.State, retained)
                            && output.RighteousFireRing?.Ring == finalRing
                            && HasCreate([output.Packet], finalRing));
                    }
                    finally { repeatedGate.Release(); }
                    await firstChange!;
                    await finalChange!;
                    ringMark = Mark();
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                    ringPackets = Since(ringMark);
                    uint currentFinalRing = Assert.IsType<uint>(retained.Ring);
                    var allRingCreates = ringPackets.Where(packet =>
                            packet.Length >= 6 && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId)
                        .ToList();
                    Assert.Equal(1, allRingCreates.Count(packet => HasCreate([packet], currentFinalRing)));
                    Assert.Equal(1, ringPackets.Count(packet => packet.SequenceEqual(
                        InstanceProtocol.EnterWakeZone(currentFinalRing))));
                    Assert.DoesNotContain(ringPackets, packet => packet.Length >= 6
                        && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId
                        && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) != currentFinalRing);

                    // A real public /gem_xp source mutation first queues the smaller retained ring. With the command
                    // and actual output drain held at the send gate, an actual unsocket retires that exact source.
                    // Both pending resize receipts must then be suppressed across the entire captured interval.
                    int loweredLevel = radiusLevels[0];
                    Assert.True(CurrentGemLevel() > loweredLevel);
                    uint oldRingBeforeSourceOff = Assert.IsType<uint>(retained.Ring);
                    int oldRadiusBeforeSourceOff = retained.Radius;
                    Assert.Equal(RadiusAt(radiusLevels[3]), oldRadiusBeforeSourceOff);
                    int sourceOffMark = Mark();
                    var sourceOffGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(sourceOffGate.Wait(0));
                    Task? lowerCommand = null, sourceOffPublish = null, resizeUnsocket = null;
                    uint resizeRing = 0;
                    try
                    {
                        lowerCommand = commands.RunAsync($"/gem_xp {rfGemType.ExperienceFor(loweredLevel)}");
                        Assert.False(lowerCommand.IsCompleted,
                            "actual /gem_xp lowering did not suspend after its synchronous radius mutation");
                        Assert.Equal(rfGemType.ExperienceFor(loweredLevel), CurrentGemExperience());
                        Assert.Equal(loweredLevel, CurrentGemLevel());
                        Assert.Same(retained, self.RighteousFire);
                        Assert.Same(sourceLease, self.GemSourceLifetimes.Current(secondSource.Identity));
                        Assert.True(global::Game.SupportedSkill(self, secondSource.Id).RequirementsMet);
                        Assert.Equal(RadiusAt(loweredLevel), retained.Radius);
                        Assert.True(retained.Radius < oldRadiusBeforeSourceOff);
                        Assert.Equal(positiveLife, self.PlayerLife);
                        Assert.Equal(positiveMana, self.PlayerMana);
                        Assert.Equal(shieldBefore, self.EnergyShield.Current);
                        Assert.Equal(existingDebt, self.PetrifiedBlood.Display(schedule.At));

                        resizeRing = Assert.IsType<uint>(retained.Ring);
                        Assert.NotEqual(oldRingBeforeSourceOff, resizeRing);
                        var pendingResize = RingOutputs(self.Minions.OutputQueue, retained, resizeRing);
                        Assert.Equal(2, pendingResize.Count);
                        Assert.Contains(pendingResize, output => HasCreate([output.Packet], resizeRing));
                        Assert.Contains(pendingResize, output => output.Packet.SequenceEqual(
                            InstanceProtocol.EnterWakeZone(resizeRing)));
                        Assert.Contains(self.Minions.OutputQueue, output => output.Packet.SequenceEqual(
                            InstanceProtocol.RemoveObject(oldRingBeforeSourceOff)));

                        sourceOffPublish = global::Game.PublishMinionOutcomes(
                            "test", wire.Stream, wire.Ciphers, self);
                        Assert.False(sourceOffPublish.IsCompleted,
                            "the actual pending resize drain did not hold at the shared send gate");
                        resizeUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self, Target(0));
                        Assert.False(resizeUnsocket.IsCompleted);
                        Assert.Null(self.RighteousFire);
                        Assert.False(self.GemSourceLifetimes.IsCurrent(sourceLease));
                        Assert.Null(self.GemSourceLifetimes.Current(secondSource.Identity));
                    }
                    finally { sourceOffGate.Release(); }
                    await lowerCommand!;
                    await sourceOffPublish!;
                    await resizeUnsocket!;
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                    var sourceOffPackets = Since(sourceOffMark);
                    Assert.False(HasCreate(sourceOffPackets, resizeRing));
                    Assert.False(HasPacket(sourceOffPackets, InstanceProtocol.EnterWakeZone(resizeRing)));
                    Assert.True(HasPacket(sourceOffPackets, InstanceProtocol.RemoveObject(oldRingBeforeSourceOff)));
                    Assert.True(HasPacket(sourceOffPackets, InstanceProtocol.RemoveObject(resizeRing)));

                    await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self, Target(0));
                    var freshSource = Source();
                    int freshSourceOffMark = Mark();
                    await Activate(freshSource);
                    var freshState = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    Assert.NotSame(retained, freshState);
                    Assert.NotNull(freshState.SourceLifetime);
                    Assert.NotSame(sourceLease, freshState.SourceLifetime);
                    Assert.Same(freshState.SourceLifetime, self.GemSourceLifetimes.Current(freshSource.Identity));
                    Assert.Equal(loweredLevel, CurrentGemLevel());
                    uint freshRing = Assert.IsType<uint>(freshState.Ring);
                    var freshSourceOffPackets = Since(freshSourceOffMark);
                    Assert.True(HasCreate(freshSourceOffPackets, freshRing));
                    Assert.True(HasPacket(freshSourceOffPackets,
                        InstanceProtocol.EnterWakeZone(freshRing)));
                }
                finally
                {
                    Program.Outgoing = previousOutgoing;
                    global::Game.AreaUpdateContext.Value = previousContext;
                    if (owner is not null)
                    {
                        owner.Closed = true;
                        PlayerInventories.Forget(character);
                        global::Game.InactiveItems.TryRemove(character, out _);
                    }
                }
            }

            async Task RighteousFireRelightPickAndDiagnosticBoundaries()
            {
                const string character = "RighteousFireRelightPickDiagnostic";
                var schedule = new AreaClockSchedule { At = 180 };
                var updates = new PlayerUpdatesState(lifetime, schedule);
                var previousContext = global::Game.AreaUpdateContext.Value;
                global::Game.AreaUpdateContext.Value = updates;
                ClientState? owner = null;
                var emitted = new List<byte[]>();
                var previousOutgoing = Program.Outgoing;
                Program.Outgoing = (seen, bytes) =>
                {
                    if (ReferenceEquals(seen, wire.Ciphers)) lock (emitted) emitted.Add(bytes.ToArray());
                    previousOutgoing?.Invoke(seen, bytes);
                };
                try
                {
                    var self = owner = new ClientState(
                        new(1, "righteous-fire-relight-pick-diagnostic", 1000, 1000, 100, 100, null, []), 12, 1)
                    {
                        Character = character, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
                        PlayerUpdatesState = updates,
                        Pools = new(100, 10000, 10000, 2, 0, Strength: 65, Dexterity: 65, Intelligence: 65),
                        LeagueModule = conn.LeagueModule,
                    };
                    self.Walk.Teleport(100, 100, schedule.At);
                    var rfFound = Program.Items.FindGem("Righteous Fire")!.Value;
                    var rfGemType = global::Game.ItemsFor(self).Gem(rfFound.Type)!;
                    int rfEffect = rfGemType.Variant(rfFound.Variant.Index).Effect;
                    Item Gem(string name)
                    {
                        var found = Program.Items.FindGem(name)!.Value;
                        return ItemCatalog.Create(found.Type) with { GemVariant = found.Variant.Index };
                    }
                    var inventory = global::Game.ItemsOf(self);
                    var wand = ItemCatalog.Create(
                        Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")!) with
                    {
                        Sockets = [new ItemSocket(SocketColour.White, Gem("Righteous Fire"))],
                        Links = [1],
                    };
                    var host = inventory.Add(PlayerInventories.Weapon1, 0, 0, wand);
                    inventory.Add(5, 0, 0, new UniqueCatalog(Program.Items).Create("Astramentis", new Random(504))!);
                    global::Game.EquipmentStatSources(self, inventory.EquipmentSnapshot());
                    self.Pools = global::Game.PoolsFor(self, self.Pools.Level);
                    self.PlayerLife = Math.Max(1u, self.Pools.Life / 2);
                    self.PlayerMana = Math.Max(1u, self.Pools.Mana / 3);
                    self.PetrifiedBlood.AddDeferred(schedule.At, 40);
                    uint positiveLife = self.PlayerLife, positiveMana = self.PlayerMana;
                    var existingDebt = self.PetrifiedBlood.Display(schedule.At);
                    IReadOnlyList<global::Game.PlayerSkillSource> Sources()
                    {
                        var equipment = inventory.EquipmentSnapshot();
                        return global::Game.PlayerSkillSourcesAt(self.Character, equipment,
                            global::Game.InactiveItems.GetValueOrDefault(self.Character), self.LeagueModule!,
                            [], global::Game.WornGrantedSkillsOf(self, equipment));
                    }
                    global::Game.PlayerSkillSource Source() => Assert.Single(
                        Sources(), source => global::Game.SkillsFor(self).EffectId(source.Effect) == "RighteousFire");
                    InstanceMicrotransactions.SkillSlot CurrentSlot(uint gemHash) =>
                        Assert.Single(self.SkillSlots.Values, value => value.GemHash == gemHash);
                    async Task<InstanceMicrotransactions.SkillSlot> RefreshSlot(uint gemHash)
                    {
                        int mark = Mark();
                        await global::Game.SendWornCosmetics("test", wire.Stream, wire.Ciphers, self,
                            $"current slot for gem 0x{gemHash:x8}");
                        var packet = Assert.Single(Since(mark), value => value.Length >= 2
                            && BinaryPrimitives.ReadUInt16BigEndian(value) == InstanceMicrotransactions.PurchasedId);
                        var current = CurrentSlot(gemHash);
                        Assert.Contains(InstanceMicrotransactions.ReadPurchased(packet).Skills,
                            value => value == current);
                        return current;
                    }
                    int CurrentGemLevel() => global::Game.ItemsFor(self).GemLevel(
                        inventory.Items(PlayerInventories.Weapon1).Single(item => item.Id == host.Id)
                            .Item.Sockets[0].Gem!);
                    int CurrentGemExperience() => inventory.Items(PlayerInventories.Weapon1)
                        .Single(item => item.Id == host.Id).Item.Sockets[0].Gem!.GemExperience;
                    uint GainToNextLevel() =>
                        (uint)Math.Max(1, (rfGemType.ExperienceFor(CurrentGemLevel() + 1) - CurrentGemExperience()) * 10);
                    int RadiusAtSkillLevel(int level)
                    {
                        var own = global::Game.SkillsFor(self).EffectStats(rfEffect, level)
                            .ToDictionary(stat => stat.Item1, stat => stat.Item2);
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                        {
                            var merged = global::Game.MergeSkillStats(global::Game.AreaPlayerStats(self), own);
                            int Get(string name, int fallback) =>
                                Program.Stats.Key(name) is uint key && merged.TryGetValue(key, out int stat)
                                    ? stat : fallback;
                            return SkillArea.Radius(Get("active_skill_base_area_of_effect_radius", 18),
                                name => Get(name, 0), spell: true);
                        }
                    }
                    InstanceItems.SocketGem Target() =>
                        new(PlayerInventories.InventoryId(PlayerInventories.Weapon1), host.Id, 0);
                    Task Activate(global::Game.PlayerSkillSource source) =>
                        global::Game.ActivateRighteousFire("test", wire.Stream, wire.Ciphers, self,
                            source.Id, source.Effect, source.Level, source.Quality, source.Gem);
                    int Mark()
                    {
                        lock (emitted) return emitted.Count;
                    }
                    List<byte[]> Since(int mark)
                    {
                        lock (emitted) return [.. emitted.Skip(mark)];
                    }
                    bool HasPacket(IEnumerable<byte[]> packets, byte[] expected) =>
                        packets.Any(packet => packet.AsSpan().IndexOf(expected) >= 0);
                    bool HasBuffAdd(IEnumerable<byte[]> packets, ushort instance, ushort row, ushort skill)
                    {
                        byte[] prefix = InstanceEntities.AddBuff(Program.PlayerId, instance, row,
                            Program.PlayerId, skill).AsSpan(0, 22).ToArray();
                        return packets.Any(packet => packet.AsSpan().IndexOf(prefix) >= 0);
                    }
                    bool HasBuffRemove(IEnumerable<byte[]> packets, ushort instance) =>
                        HasPacket(packets, InstanceEntities.RemoveBuff(Program.PlayerId, instance));
                    bool HasCreate(IEnumerable<byte[]> packets, uint id) =>
                        packets.Any(packet => packet.Length >= 6
                            && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceEntities.CreateObjectId
                            && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == id);
                    List<PlayerMinions.Output> RingOutputs(RighteousFireState state, uint ring) =>
                        self.Minions.OutputQueue.Where(output => output.Kind == PlayerMinions.OutputKind.RighteousFireRing
                            && ReferenceEquals(output.RighteousFireRing?.State, state)
                            && output.RighteousFireRing.Ring == ring).ToList();
                    (uint Key, uint RecordId) SkillPick(uint key, uint record)
                    {
                        var packet = new byte[InstanceMicrotransactions.SkillPickLength];
                        BinaryPrimitives.WriteUInt16BigEndian(packet, InstanceMicrotransactions.SkillPickId);
                        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), key);
                        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6), record);
                        return InstanceMicrotransactions.ReadSkillPick(packet);
                    }
                    var replies = new List<string>();
                    var commands = new ChatCommands
                    {
                        Send = (bytes, label) => Program.Send("test", wire.Stream, wire.Ciphers, bytes, label),
                        Reply = text => { replies.Add(text); return Task.CompletedTask; },
                    };
                    self.Commands = ConditionalSourceTestDrivers.Register(commands, "test", wire.Stream, wire.Ciphers, self);

                    var source = Source();
                    await Activate(source);
                    var live = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    var sourceLease = Assert.IsType<GemSourceLifetimes.SourceLifetime>(live.SourceLifetime);
                    Assert.Same(sourceLease, self.GemSourceLifetimes.Current(source.Identity));
                    uint liveLife = self.PlayerLife, liveMana = self.PlayerMana;
                    uint liveShield = self.EnergyShield.Current;
                    Assert.Equal(positiveLife, liveLife);
                    Assert.Equal(positiveMana, liveMana);
                    Assert.Equal(existingDebt, self.PetrifiedBlood.Display(schedule.At));

                    // Real 0x1f3 publication from the socket creates the transient skill-slot key used by 0x1fa.
                    int slotMark = Mark();
                    await global::Game.SendWornCosmetics("test", wire.Stream, wire.Ciphers, self, "relight fixture");
                    var slotPacket = Assert.Single(Since(slotMark), packet => packet.Length >= 2
                        && BinaryPrimitives.ReadUInt16BigEndian(packet) == InstanceMicrotransactions.PurchasedId);
                    var published = InstanceMicrotransactions.ReadPurchased(slotPacket);
                    var slot = CurrentSlot(source.Gem);
                    Assert.Contains(published.Skills, value => value == slot);
                    var gemBase = global::Game.ItemsFor(self).ByHash(source.Gem)!;
                    var artKey = Program.Stats.Key("skill_art_variation")!.Value;
                    var compatible = global::Game.Mtx.Owned.Where(record =>
                        global::Game.MtxEffects.FitsSkill(record.MtxTypeHash, gemBase, slot.SlotType)).ToList();
                    var artRecord = compatible.FirstOrDefault(record =>
                        global::Game.MtxEffects.Of(record.MtxTypeHash)?.Stats.Any(pair => pair.Stat == artKey) == true);
                    int ownedRecords = global::Game.Mtx.Owned.Count;
                    Assert.True(artRecord is not null,
                        $"measured blocker: {ownedRecords} owned records, {compatible.Count} fit " +
                        $"Righteous Fire slot type {slot.SlotType}, but none supports skill_art_variation");
                    var selectedArt = artRecord!;
                    int artVariation = global::Game.MtxEffects.Of(selectedArt.MtxTypeHash)!.Stats
                        .First(pair => pair.Stat == artKey).Value;

                    // /set_aoe writes the real neutral area stat under mutation and the central profile boundary must
                    // resize the same RF owner before its first output send.
                    var areaStat = Program.Stats.Key("base_skill_area_of_effect_+%")!.Value;
                    int RadiusWithAoe(int value)
                    {
                        using (new global::Game.PlayerStatScope(self, fresh: true))
                        {
                            var supported = global::Game.SupportedSkill(self, source.Id);
                            var merged = global::Game.MergeSkillStats(global::Game.AreaPlayerStats(self), supported.Stats);
                            merged[areaStat] = value;
                            int Get(string name, int fallback) =>
                                Program.Stats.Key(name) is uint key && merged.TryGetValue(key, out int stat)
                                    ? stat : fallback;
                            return SkillArea.Radius(Get("active_skill_base_area_of_effect_radius", 18),
                                name => Get(name, 0), spell: true);
                        }
                    }
                    int baselineRadius = live.Radius;
                    int aoe = new[] { 50, 100, 200 }.First(value => RadiusWithAoe(value) > baselineRadius + 2);
                    int expandedRadius = RadiusWithAoe(aoe);
                    var variety = global::Game.Monsters.Find("Rhoa");
                    Assert.NotNull(variety);
                    int targetX = 100 + expandedRadius + variety!.ObjectSize;
                    uint targetId = await global::Game.SpawnMonster("test", wire.Stream, wire.Ciphers, self,
                        variety, 70, MonsterRarity.Normal, targetX, 100, name: "Righteous Fire area boundary Rhoa");
                    var target = self.Monsters[targetId];
                    double distance = global::Game.Dist((100, 100), target.Walk.Position(schedule.At));
                    Assert.True(distance > baselineRadius + target.StopRange);
                    Assert.True(distance <= expandedRadius + target.StopRange);
                    int aoeMark = Mark();
                    var aoeGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(aoeGate.Wait(0));
                    Task? setArea = null;
                    uint baselineRing = Assert.IsType<uint>(live.Ring);
                    try
                    {
                        setArea = commands.RunAsync($"/set_aoe {aoe}");
                        Assert.False(setArea.IsCompleted);
                        Assert.Equal(aoe, self.PlayerStats[areaStat]);
                        Assert.Same(live, self.RighteousFire);
                        Assert.Same(sourceLease, self.GemSourceLifetimes.Current(source.Identity));
                        Assert.Equal(expandedRadius, live.Radius);
                        uint expandedRing = Assert.IsType<uint>(live.Ring);
                        Assert.NotEqual(baselineRing, expandedRing);
                        var pending = RingOutputs(live, expandedRing);
                        Assert.Equal(2, pending.Count);
                        Assert.Contains(pending, output => HasCreate([output.Packet], expandedRing));
                        Assert.Contains(pending, output => output.Packet.SequenceEqual(
                            InstanceProtocol.EnterWakeZone(expandedRing)));
                        Assert.Contains(self.Minions.OutputQueue, output => output.Packet.SequenceEqual(
                            InstanceProtocol.RemoveObject(baselineRing)));
                        Assert.Equal(liveLife, self.PlayerLife);
                        Assert.Equal(liveMana, self.PlayerMana);
                        Assert.Equal(liveShield, self.EnergyShield.Current);
                        Assert.Equal(existingDebt, self.PetrifiedBlood.Display(schedule.At));
                        Assert.Empty(Since(aoeMark));
                    }
                    finally { aoeGate.Release(); }
                    await setArea!;
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                    var aoePackets = Since(aoeMark);
                    uint expandedRingAfterDrain = Assert.IsType<uint>(live.Ring);
                    Assert.True(HasCreate(aoePackets, expandedRingAfterDrain));
                    Assert.True(HasPacket(aoePackets, InstanceProtocol.EnterWakeZone(expandedRingAfterDrain)));
                    Assert.False(HasBuffAdd(aoePackets, live.Buff, global::Game.RighteousFireBuffRow, live.Skill));
                    Assert.False(HasBuffAdd(aoePackets, live.SelfAura, global::Game.RighteousFireAuraBuffRow, live.Skill));
                    await global::Game.BurnAround("test", wire.Stream, wire.Ciphers, self, live, .25);
                    Assert.Contains(targetId, live.Auras.Keys);
                    uint areaLife = self.PlayerLife, areaMana = self.PlayerMana;
                    uint areaShield = self.EnergyShield.Current;
                    var areaDebt = self.PetrifiedBlood.Display(schedule.At);

                    // off returns to the character's own area profile without ending or recasting the exact RF owner.
                    int offMark = Mark();
                    var offGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(offGate.Wait(0));
                    Task? areaOff = null;
                    uint beforeOffRing = Assert.IsType<uint>(live.Ring);
                    try
                    {
                        areaOff = commands.RunAsync("/set_aoe off");
                        Assert.False(areaOff.IsCompleted);
                        Assert.False(self.PlayerStats.ContainsKey(areaStat));
                        Assert.Same(live, self.RighteousFire);
                        Assert.Equal(baselineRadius, live.Radius);
                        Assert.Same(sourceLease, self.GemSourceLifetimes.Current(source.Identity));
                        Assert.Equal(areaLife, self.PlayerLife);
                        Assert.Equal(areaMana, self.PlayerMana);
                        Assert.Equal(areaShield, self.EnergyShield.Current);
                        Assert.Equal(areaDebt, self.PetrifiedBlood.Display(schedule.At));
                        uint offRing = Assert.IsType<uint>(live.Ring);
                        Assert.NotEqual(beforeOffRing, offRing);
                        Assert.Equal(2, RingOutputs(live, offRing).Count);
                        Assert.Empty(Since(offMark));
                    }
                    finally { offGate.Release(); }
                    await areaOff!;
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                    var offPackets = Since(offMark);
                    Assert.True(HasCreate(offPackets, Assert.IsType<uint>(live.Ring)));
                    Assert.Equal("Increased area of effect: the character's own", replies[^1]);
                    Assert.DoesNotContain(replies, reply => reply.Contains("relit", StringComparison.OrdinalIgnoreCase));

                    // /god is a supplemental diagnostic: the central boundary resizes the same owner, while the command's
                    // deliberate on-heal remains. Source-off followed by /god off must not resurrect RF.
                    int godMark = Mark();
                    var godGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(godGate.Wait(0));
                    Task? godOn = null;
                    try
                    {
                        godOn = commands.RunAsync("/god on");
                        Assert.False(godOn.IsCompleted);
                        Assert.True(self.God);
                        Assert.Same(live, self.RighteousFire);
                        Assert.Same(sourceLease, self.GemSourceLifetimes.Current(source.Identity));
                        Assert.Equal(live.Buff, self.RighteousFire!.Buff);
                        Assert.Equal(live.SelfAura, self.RighteousFire!.SelfAura);
                        Assert.True(live.Radius > baselineRadius);
                        Assert.Equal(global::Game.LifeCap(self), self.PlayerLife);
                        Assert.Equal(2, RingOutputs(live, Assert.IsType<uint>(live.Ring)).Count);
                        Assert.Empty(Since(godMark));
                    }
                    finally { godGate.Release(); }
                    await godOn!;
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                    Assert.Same(live, self.RighteousFire);
                    Assert.Equal("God mode on.", replies[^1]);
                    await global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self, Target());
                    Assert.Null(self.RighteousFire);
                    await commands.RunAsync("/god off");
                    Assert.False(self.God);
                    Assert.Null(self.RighteousFire);
                    await global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self, Target());
                    var cosmeticSource = Source();
                    slot = await RefreshSlot(cosmeticSource.Gem);
                    Assert.True(global::Game.MtxEffects.FitsSkill(
                        selectedArt.MtxTypeHash, global::Game.ItemsFor(self).ByHash(slot.GemHash)!, slot.SlotType));
                    await Activate(cosmeticSource);
                    var cosmeticState = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    var cosmeticLease = Assert.IsType<GemSourceLifetimes.SourceLifetime>(cosmeticState.SourceLifetime);
                    Assert.Same(cosmeticLease, self.GemSourceLifetimes.Current(cosmeticSource.Identity));

                    // Actual handler: its save and expected RF owner commit before its first network await. Replacing
                    // the physical source and casting a same-id RF while held must prevent the old pick from relighting.
                    int stalePickMark = Mark();
                    var stalePickGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(stalePickGate.Wait(0));
                    Assert.True(global::Game.CurrentGameWork);
                    Assert.True(self.SkillSlots.ContainsKey(slot.Key));
                    Assert.Contains((slot.Inventory, slot.Socket, slot.GemHash, slot.SlotType),
                        global::Game.SkillCosmeticPlaces(self));
                    Task? stalePick = null, pickUnsocket = null, pickReinsert = null, pickActivate = null;
                    RighteousFireState pickReplacement = null!;
                    try
                    {
                        stalePick = GameServer.Modules.CosmeticsAndPets.CosmeticsFeature.PickSkillCosmetic(
                            "test", wire.Stream, wire.Ciphers, self, SkillPick(slot.Key, selectedArt.Id));
                        bool currentWork = global::Game.CurrentGameWork;
                        bool slotCurrent = self.SkillSlots.ContainsKey(slot.Key);
                        bool savedCurrent = CharacterSaves.SkillCosmetics(character).ContainsKey(
                            CharacterSaves.SkillCosmeticKey(slot.GemHash, slot.SlotType));
                        Assert.False(stalePick.IsCompleted,
                            $"actual cosmetic pick finished before its held output: status {stalePick.Status}, " +
                            $"work {currentWork}, slot {slotCurrent}, saved {savedCurrent}");
                        Assert.True(CharacterSaves.SkillCosmetics(character).TryGetValue(
                            CharacterSaves.SkillCosmeticKey(slot.GemHash, slot.SlotType), out var saved));
                        Assert.Equal(selectedArt.Id, saved.Record);
                        pickUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self, Target());
                        Assert.False(pickUnsocket.IsCompleted);
                        Assert.Null(self.RighteousFire);
                        Assert.False(self.GemSourceLifetimes.IsCurrent(cosmeticLease));
                        pickReinsert = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self, Target());
                        Assert.False(pickReinsert.IsCompleted);
                        var replacementSource = Source();
                        pickActivate = Activate(replacementSource);
                        Assert.False(pickActivate.IsCompleted);
                        pickReplacement = Assert.IsType<RighteousFireState>(self.RighteousFire);
                        Assert.NotSame(cosmeticState, pickReplacement);
                        Assert.NotSame(cosmeticLease, pickReplacement.SourceLifetime);
                    }
                    finally { stalePickGate.Release(); }
                    await stalePick!;
                    await pickUnsocket!;
                    await pickReinsert!;
                    await pickActivate!;
                    Assert.Same(pickReplacement, self.RighteousFire);
                    Assert.Same(pickReplacement.SourceLifetime, self.GemSourceLifetimes.Current(Source().Identity));
                    Assert.Equal(artVariation, global::Game.SkillArtVariation(self, slot.GemHash));
                    Assert.Equal(global::Game.Art.BuffVisual("righteous_fire_aura_visual", artVariation), pickReplacement.AuraVisual);
                    Assert.Equal(global::Game.Art.Animated("righteous_fire_ring", artVariation) ?? global::Game.RighteousFireRingAo,
                        pickReplacement.RingArt);
                    var stalePickPackets = Since(stalePickMark);
                    Assert.False(HasBuffRemove(stalePickPackets, pickReplacement.Buff));
                    Assert.False(HasBuffRemove(stalePickPackets, pickReplacement.SelfAura));
                    slot = await RefreshSlot(Source().Gem);
                    Assert.True(global::Game.MtxEffects.FitsSkill(
                        selectedArt.MtxTypeHash, global::Game.ItemsFor(self).ByHash(slot.GemHash)!, slot.SlotType));

                    // A pick made while RF is absent captures null and must not bind a new RF cast during its awaits.
                    int absentPickMark = Mark();
                    var absentPickGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(absentPickGate.Wait(0));
                    Task? absentPick = null, absentEnd = null, absentActivate = null;
                    RighteousFireState absentReplacement = null!;
                    try
                    {
                        var absentSource = Source();
                        absentEnd = global::Game.EndRighteousFire("test", wire.Stream, wire.Ciphers, self,
                            "null-owner pick fixture", pickReplacement, absentSource, pickReplacement.Instance);
                        Assert.False(absentEnd.IsCompleted);
                        Assert.Null(self.RighteousFire);
                        Assert.Same(pickReplacement.SourceLifetime, self.GemSourceLifetimes.Current(absentSource.Identity));
                        absentPick = GameServer.Modules.CosmeticsAndPets.CosmeticsFeature.PickSkillCosmetic(
                            "test", wire.Stream, wire.Ciphers, self, SkillPick(slot.Key, selectedArt.Id));
                        Assert.False(absentPick.IsCompleted);
                        absentActivate = Activate(absentSource);
                        Assert.False(absentActivate.IsCompleted);
                        absentReplacement = Assert.IsType<RighteousFireState>(self.RighteousFire);
                        Assert.NotSame(pickReplacement, absentReplacement);
                        Assert.Same(pickReplacement.SourceLifetime, absentReplacement.SourceLifetime);
                    }
                    finally { absentPickGate.Release(); }
                    await absentEnd!;
                    await absentPick!;
                    await absentActivate!;
                    Assert.Same(absentReplacement, self.RighteousFire);
                    var absentPickPackets = Since(absentPickMark);
                    Assert.False(HasBuffRemove(absentPickPackets, absentReplacement.Buff));
                    Assert.False(HasBuffRemove(absentPickPackets, absentReplacement.SelfAura));
                    slot = CurrentSlot(Source().Gem);

                    // Record 0 is a legal actual unpick control: the same physical source is restyled to variation 0,
                    // so only the presentation owner is replaced.
                    int unpickMark = Mark();
                    await GameServer.Modules.CosmeticsAndPets.CosmeticsFeature.PickSkillCosmetic(
                        "test", wire.Stream, wire.Ciphers, self, SkillPick(slot.Key, 0));
                    Assert.False(CharacterSaves.SkillCosmetics(character).ContainsKey(
                        CharacterSaves.SkillCosmeticKey(slot.GemHash, slot.SlotType)));
                    var unpicked = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    Assert.NotSame(absentReplacement, unpicked);
                    Assert.Same(unpicked.SourceLifetime, self.GemSourceLifetimes.Current(Source().Identity));
                    Assert.Equal(0, global::Game.SkillArtVariation(self, slot.GemHash));
                    var unpickPackets = Since(unpickMark);
                    Assert.True(HasBuffAdd(unpickPackets, unpicked.Buff, global::Game.RighteousFireBuffRow, unpicked.Skill));
                    Assert.True(HasBuffAdd(unpickPackets, unpicked.SelfAura, global::Game.RighteousFireAuraBuffRow, unpicked.Skill));

                    // Same physical lease, newer gem item and level: the held cleanup must admit the current supported
                    // profile rather than retain its old item snapshot or leave RF absent.
                    int profileMark = Mark();
                    var profileGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(profileGate.Wait(0));
                    int levelBefore = CurrentGemLevel();
                    Assert.InRange(levelBefore, 1, rfGemType.MaxLevel - 1);
                    int progressedLevel = levelBefore;
                    Task? heldProfileRelight = null, normalExperience = null;
                    try
                    {
                        heldProfileRelight = global::Game.RelightRighteousFire("test", wire.Stream, wire.Ciphers, self,
                            "same-lease progression", unpicked, slot.GemHash);
                        Assert.False(heldProfileRelight.IsCompleted);
                        Assert.Null(self.RighteousFire);
                        normalExperience = Task.WhenAll(global::Game.GainGemExperience("test", wire.Stream, wire.Ciphers, self, GainToNextLevel()),
                            global::Game.LevelUpAllGems("test", wire.Stream, wire.Ciphers, self));
                        Assert.False(normalExperience.IsCompleted);
                        progressedLevel = CurrentGemLevel();
                        Assert.Equal(levelBefore + 1, progressedLevel);
                        Assert.Null(self.RighteousFire);
                        Assert.Same(unpicked.SourceLifetime, self.GemSourceLifetimes.Current(Source().Identity));
                        Assert.Equal(progressedLevel, Source().Level);
                    }
                    finally { profileGate.Release(); }
                    await heldProfileRelight!;
                    await normalExperience!;
                    await global::Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, self);
                    var currentProfile = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    Assert.NotSame(unpicked, currentProfile);
                    Assert.Same(unpicked.SourceLifetime, currentProfile.SourceLifetime);
                    Assert.Equal(progressedLevel, Source().Level);
                    Assert.Equal(RadiusAtSkillLevel(progressedLevel), currentProfile.Radius);
                    var profilePackets = Since(profileMark);
                    Assert.True(HasBuffAdd(profilePackets, currentProfile.Buff,
                        global::Game.RighteousFireBuffRow, currentProfile.Skill));
                    Assert.True(HasBuffAdd(profilePackets, currentProfile.SelfAura,
                        global::Game.RighteousFireAuraBuffRow, currentProfile.Skill));

                    // Helper-only ABA control: after the expected owner detaches, source-off/reinsert without a cast
                    // leaves no RF when the old continuation is released. A fresh actual cast is then positive.
                    int helperMark = Mark();
                    var helperGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(helperGate.Wait(0));
                    Task? heldRelight = null, helperUnsocket = null, helperReinsert = null;
                    try
                    {
                        heldRelight = global::Game.RelightRighteousFire("test", wire.Stream, wire.Ciphers, self,
                            "helper owner ABA", currentProfile, slot.GemHash);
                        Assert.False(heldRelight.IsCompleted);
                        Assert.Null(self.RighteousFire);
                        helperUnsocket = global::Game.UnsocketGem("test", wire.Stream, wire.Ciphers, self, Target());
                        Assert.False(helperUnsocket.IsCompleted);
                        helperReinsert = global::Game.SocketGem("test", wire.Stream, wire.Ciphers, self, Target());
                        Assert.False(helperReinsert.IsCompleted);
                    }
                    finally { helperGate.Release(); }
                    await heldRelight!;
                    await helperUnsocket!;
                    await helperReinsert!;
                    Assert.Null(self.RighteousFire);
                    var helperPackets = Since(helperMark);
                    Assert.True(HasBuffRemove(helperPackets, currentProfile.Buff));
                    Assert.True(HasBuffRemove(helperPackets, currentProfile.SelfAura));
                    Assert.DoesNotContain(helperPackets, packet => packet.Length >= 2
                        && BinaryPrimitives.ReadUInt16BigEndian(packet) == 0x1c1);
                    var positiveSource = Source();
                    await Activate(positiveSource);
                    var positive = Assert.IsType<RighteousFireState>(self.RighteousFire);
                    Assert.Same(positive.SourceLifetime, self.GemSourceLifetimes.Current(positiveSource.Identity));
                }
                finally
                {
                    Program.Outgoing = previousOutgoing;
                    global::Game.AreaUpdateContext.Value = previousContext;
                    if (owner is not null)
                    {
                        owner.Closed = true;
                        PlayerInventories.Forget(character);
                        global::Game.InactiveItems.TryRemove(character, out _);
                    }
                }
            }

            const string resourceCharacter = "IntegerResourceAuthority";
            var resourceSchedule = new AreaClockSchedule();
            var resourceUpdates = new PlayerUpdatesState(lifetime, resourceSchedule);
            var previousResourceContext = global::Game.AreaUpdateContext.Value;
            global::Game.AreaUpdateContext.Value = resourceUpdates;
            ClientState? resource = null;
            var extraResourceClients = new List<ClientState>();
            try
            {
                resourceSchedule.At = 20;
                resource = new ClientState(new(1, "integer-resource-authority", 1000, 1000, 100, 100, null, []), 6, 1)
                {
                    Character = resourceCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                    PlayerSent = true, Experience = CharacterStats.ExperienceFor(100), PlayerUpdatesState = resourceUpdates,
                    Pools = new(100, 10000, 10000, 2, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    LeagueModule = conn.LeagueModule,
                };
                resource.Golems.Restored = true;
                foreach (string stat in new[] { "virtual_strength_for_requirements",
                             "virtual_dexterity_for_requirements", "virtual_intelligence_for_requirements" })
                    resource.PlayerStats[Program.Stats.Key(stat)!.Value] = 1000;
                resource.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                resource.PlayerStats[Program.Stats.Key("minion_life_regeneration_rate_per_minute_%")!.Value] = -60;
                resource.PlayerStats[Program.Stats.Key("minion_additional_spell_block_%")!.Value] = 0;
                resource.PlayerStats[Program.Stats.Key("minion_block_%")!.Value] = 0;

                var resourceInventory = Game.ItemsOf(resource);
                var resourceGem = ItemCatalog.Create(
                    Program.Items.Find("Metadata/Items/Gems/SkillGemSummonFlameGolem")!);
                var treeProvider = Program.Items.Mods.ById(
                    "WeaponTreeMinionLifeAsExtraEnergyShieldReducedElementalResistance1")!;
                var treeRoll = treeProvider.Roll(ModSection.Explicit, new Random(504));
                Assert.Equal(15, treeRoll.Values[0]);
                var resourceWeapon = ItemCatalog.Create(
                    Program.Items.Find("Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!) with
                {
                    Sockets = [new(SocketColour.White, resourceGem)],
                    Mods = [.. ItemCatalog.Create(Program.Items.Find(
                        "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow1")!).Mods, treeRoll],
                };
                var weaponSlot = resourceInventory.Add(PlayerInventories.Weapon1, 0, 0, resourceWeapon);

                var resourceArmour = new UniqueCatalog(Program.Items).Create("Fleshcrafter", new Random(504))!;
                bool IsRow(ItemMod mod, string id) => Program.Items.Mods.ByHash(mod.Hash)?.Id == id;
                const string conversionRow = "MinionLifeConvertedToEnergyShieldUnique__1";
                const string noBypassRow = "MinionChaosDamageDoesNotBypassESUnique__1";
                const string delayRow = "MinionEnergyShieldRechargeDelayUnique__1";
                var conversion = resourceArmour.Mods.Single(mod => IsRow(mod, conversionRow));
                var noBypass = resourceArmour.Mods.Single(mod => IsRow(mod, noBypassRow));
                var delay = resourceArmour.Mods.Single(mod => IsRow(mod, delayRow));
                Assert.Equal(2, conversion.Values[0]);
                Assert.Equal(1, noBypass.Values[0]);
                Assert.InRange(delay.Values[0], 50, 100);
                var originalArmourMods = resourceArmour.Mods.ToArray();
                var wornArmour = resourceArmour with
                {
                    Mods = originalArmourMods.Where(mod => !IsRow(mod, noBypassRow)).ToArray(),
                };
                var armourSlot = resourceInventory.Add(PlayerInventories.BodyArmour, 0, 0, wornArmour);

                void RecalculateResource()
                {
                    Game.EquipmentStatSources(resource, resourceInventory.EquipmentSnapshot());
                    resource.Pools = Game.PoolsFor(resource, resource.Pools.Level);
                    resource.PlayerLife = resource.Pools.Life;
                    resource.PlayerMana = resource.Pools.Mana;
                }
                void SetDelay(int value)
                {
                    resourceInventory.Change(PlayerInventories.BodyArmour, armourSlot.Id, item => item with
                    {
                        Mods = item.Mods.Select(mod => mod.Hash == delay.Hash
                            ? mod with { Values = [value] } : mod).ToArray(),
                    });
                    RecalculateResource();
                }
                RecalculateResource();
                var resourceSource = Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonFlameGolem");
                var resourceDefinition = Game.GolemData.Find("SummonFlameGolem")!;
                uint ExpectedMaximum(GolemState.Golem golem)
                {
                    var pools = Game.GolemPools.Calculate(Game.Monsters, golem.Monster.Variety!, golem.Monster.Level,
                        golem, golem.Player, golem.Monster.Mods.Effects);
                    return Game.NativeEnergyShield(pools.MaximumEnergyShield);
                }
                void AssertIntegerPool(GolemState.Golem golem)
                {
                    Assert.InRange(golem.MaximumShield, 0u, (uint)int.MaxValue);
                    Assert.InRange(golem.Shield, 0u, golem.MaximumShield);
                    Assert.Equal(ExpectedMaximum(golem), golem.MaximumShield);
                }
                void AssertConservation(GolemState.Golem golem, uint lifeBefore, uint shieldBefore,
                    uint lifeAfter, uint shieldAfter, int mitigated)
                {
                    long absorbed = (long)shieldBefore - shieldAfter;
                    long lifeLost = (long)lifeBefore - lifeAfter;
                    Assert.Equal((long)mitigated, absorbed + lifeLost);
                }
                async Task<List<byte[]>> CaptureResource(Func<Task> send)
                {
                    var packets = new List<byte[]>();
                    var previous = Program.Outgoing;
                    Program.Outgoing = (seen, bytes) =>
                    {
                        if (ReferenceEquals(seen, wire.Ciphers)) lock (packets) packets.Add(bytes.ToArray());
                        previous?.Invoke(seen, bytes);
                    };
                    try { await send(); }
                    finally { Program.Outgoing = previous; }
                    return packets;
                }
                ClientState NewResourceClient(string name, double at)
                {
                    resourceSchedule.At = at;
                    var client = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), 8, 1)
                    {
                        Character = resourceCharacter, Instance = new(), PlayerLife = 10000, PlayerMana = 10000,
                        PlayerSent = true, Experience = CharacterStats.ExperienceFor(100),
                        PlayerUpdatesState = resourceUpdates,
                        Pools = new(resource.Pools.Level, 10000, 10000, 2, 0,
                            Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                    };
                    foreach (var stat in resource.PlayerStats) client.PlayerStats[stat.Key] = stat.Value;
                    Game.EquipmentStatSources(client, resourceInventory.EquipmentSnapshot());
                    client.Pools = Game.PoolsFor(client, client.Pools.Level);
                    client.PlayerLife = client.Pools.Life; client.PlayerMana = client.Pools.Mana;
                    extraResourceClients.Add(client);
                    return client;
                }
                void AssertCreateResources(IReadOnlyList<byte[]> packets, uint id, uint currentLife,
                    uint currentShield, uint maximumLife, uint maximumShield)
                {
                    var create = Assert.Single(packets, bytes => bytes.Length >= 18
                        && BinaryPrimitives.ReadUInt16BigEndian(bytes) == InstanceEntities.CreateObjectId
                        && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) == id);
                    int blobLength = BinaryPrimitives.ReadUInt16BigEndian(create.AsSpan(16));
                    var blob = create.AsSpan(18, blobLength).ToArray();
                    int cursor = 20;
                    uint Compact()
                    {
                        byte first = blob[cursor++];
                        int following = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
                        uint value = following == 4 ? 0u : (uint)(first & (0x7f >> following));
                        for (int i = 0; i < following; i++) value = (value << 8) | blob[cursor++];
                        return value;
                    }
                    var stats = new Dictionary<uint, uint>();
                    for (uint count = Compact(), i = 0; i < count; i++) stats[Compact()] = Compact();
                    Assert.Equal(maximumLife, stats[StatKeys.MaximumLife]);
                    Assert.Equal(maximumShield, stats[maximumEnergyShield]);
                    byte[] Pool(uint current)
                    {
                        var pool = new byte[18];
                        BinaryPrimitives.WriteUInt32LittleEndian(pool, current);
                        return pool;
                    }
                    Assert.True(blob.AsSpan().IndexOf([.. Pool(currentLife), .. Pool(0), .. Pool(currentShield)]) >= 0);
                }

                // Labelled neutral diagnostics: moving preserves the absolute instance and fractional integration state.
                // The remaining second is integral, while expiry discards the last unbanked fraction.
                var neutralIgnite = new MonsterIgnites();
                var movedIgnite = new MonsterIgnites();
                Assert.True(neutralIgnite.Add(10, .75, 3));
                Assert.Equal(0, neutralIgnite.Advance(10.5, 1));
                neutralIgnite.MoveTo(movedIgnite);
                Assert.True(neutralIgnite.Idle);
                Assert.False(movedIgnite.Idle);
                Assert.Equal(13, movedIgnite.Until);
                Assert.Equal(1, movedIgnite.Advance(11.5, 1, 0, out double? movedIgniteActivity));
                Assert.Equal(11.5, movedIgniteActivity);
                Assert.False(movedIgnite.Idle);
                Assert.Equal(1, movedIgnite.Advance(13, 1, 0, out movedIgniteActivity));
                Assert.Equal(13, movedIgniteActivity);
                Assert.True(movedIgnite.Idle);
                Assert.Equal(0, neutralIgnite.Advance(13, 1, 0, out double? sourceIgniteActivity));
                Assert.Null(sourceIgniteActivity);
                var untouchedIgnite = new MonsterIgnites();
                Assert.True(untouchedIgnite.Add(10, .75, 3));
                Assert.Equal(2, untouchedIgnite.Advance(13, 1));
                Assert.True(untouchedIgnite.Idle);

                var neutralPoison = new MonsterPoisons();
                var movedPoison = new MonsterPoisons();
                Assert.True(neutralPoison.Add(10, .75, 3));
                Assert.Equal(0, neutralPoison.Advance(10.5, 1));
                neutralPoison.MoveTo(movedPoison);
                Assert.True(neutralPoison.Idle);
                Assert.Equal(13, movedPoison.Until);
                Assert.Equal(1, movedPoison.Advance(11.5, 1, out double? movedPoisonActivity));
                Assert.Equal(11.5, movedPoisonActivity);
                Assert.False(movedPoison.Idle);
                Assert.Equal(1, movedPoison.Advance(13, 1, out movedPoisonActivity));
                Assert.Equal(13, movedPoisonActivity);
                Assert.True(movedPoison.Idle);
                Assert.Equal(0, neutralPoison.Advance(13, 1, out double? sourcePoisonActivity));
                Assert.Null(sourcePoisonActivity);
                var untouchedPoison = new MonsterPoisons();
                Assert.True(untouchedPoison.Add(10, .75, 3));
                Assert.Equal(2, untouchedPoison.Advance(13, 1));
                Assert.True(untouchedPoison.Idle);

                var neutralElemental = new MonsterElementalAilments();
                Assert.NotNull(neutralElemental.WithoutDamage(MonsterElementalAilments.Kind.Chill, 3, 10,
                    _ => 0, _ => 0, area: 0));
                Assert.NotNull(neutralElemental.Freeze(2, 10, _ => 0, _ => 0));
                Assert.NotNull(neutralElemental.WithoutDamage(MonsterElementalAilments.Kind.Shock, 4, 10,
                    _ => 0, _ => 0, area: 0));
                Assert.Equal(3, neutralElemental.Present(10).Count);
                var movedElemental = new MonsterElementalAilments();
                neutralElemental.MoveTo(movedElemental);
                Assert.True(neutralElemental.Idle);
                Assert.Equal(3, movedElemental.Present(10).Count);
                Assert.Equal(0, movedElemental.ActionFactor(10));
                Assert.Equal(2, movedElemental.Remaining(MonsterElementalAilments.Kind.Freeze, 10));

                resourceSchedule.At = 20.25;
                var freshResourcePackets = await CaptureResource(() => Game.SummonGolem("test", wire.Stream,
                    wire.Ciphers, resource, resourceSource.Id, resourceDefinition, (100, 100)));
                Assert.NotEmpty(freshResourcePackets);
                var freshResource = Assert.Single(resource.Golems.All).Value;
                Assert.Equal(freshResource.Monster.MaxLife, freshResource.Monster.Life);
                Assert.Equal(freshResource.MaximumShield, freshResource.Shield);
                Assert.True(freshResource.MaximumShield > 0);
                AssertCreateResources(freshResourcePackets, freshResource.Id, freshResource.Monster.Life,
                    freshResource.Shield, freshResource.Monster.MaxLife, freshResource.MaximumShield);

                // Labelled model diagnostic: unconditional hit ignore is separate from critical ignore. It zeroes
                // elemental resistance (including negative resistance) from any hit, removes penetration, and the
                // real suppressor wins. Physical, chaos and OverTime retain their ordinary resistance.
                static int ModelDamage(DamageType type, DamageModel.ResistanceValue target,
                    HitResistances.Mods mods, bool overTime = false)
                {
                    var strike = new HitResistances.Strike(Critical: false, Attack: true, OverTime: overTime);
                    var mitigated = DamageModel.Mitigate(DamagePacket.Of(type, 400, 400),
                        damage => HitResistances.Effective(damage, target, strike, mods));
                    return (int)Math.Round(mitigated[type].Min);
                }
                var unconditional = new HitResistances.Mods(0, 0, 0, 0, UnconditionalIgnoreElemental: true);
                var suppressed = new HitResistances.Mods(0, 0, 0, 1, true);
                Assert.Equal(340, ModelDamage(DamageType.Fire, new(25, 75, 10), default));
                Assert.Equal(400, ModelDamage(DamageType.Fire, new(25, 75, 10), unconditional));
                Assert.Equal(440, ModelDamage(DamageType.Fire, new(0, 75, 10), default));
                Assert.Equal(400, ModelDamage(DamageType.Fire, new(0, 75, 10), unconditional));
                Assert.Equal(540, ModelDamage(DamageType.Fire, new(-25, 75, 10), default));
                Assert.Equal(400, ModelDamage(DamageType.Fire, new(-25, 75, 10), unconditional));
                Assert.Equal(500, ModelDamage(DamageType.Fire, new(-25, 75, 10), suppressed));
                Assert.Equal(300, ModelDamage(DamageType.Physical, new(25), unconditional));
                Assert.Equal(300, ModelDamage(DamageType.Chaos, new(25), unconditional));
                Assert.Equal(300, ModelDamage(DamageType.Fire, new(25), unconditional, overTime: true));

                // Actual catalogue Fleshcrafter + tree Energy Shield + real summoned Flame Golem. The fixed current
                // ability is a labelled diagnostic after the real source/ability refresh. Target resistance is supplied
                // through the target's ordinary mod effects; no artificial ignore field or target mutation is used.
                var preservedArmourItem = resourceInventory.Items(PlayerInventories.BodyArmour).Single().Item;
                var preservedWeaponItem = resourceInventory.Items(PlayerInventories.Weapon1).Single().Item;
                var preservedPlayerStats = new Dictionary<uint, int>(resource.PlayerStats);
                var preservedIncomingRandom = resource.IncomingHitRandom;
                var preservedAreaTime = resourceSchedule.At;
                var preservedGolemWalk = freshResource.Monster.Walk;
                var preservedGolemTarget = freshResource.Target;
                var preservedAggro = freshResource.Monster.Aggro;
                var preservedLastTarget = freshResource.Monster.LastTarget;
                var preservedLife = freshResource.Monster.Life;
                var preservedMaximumLife = freshResource.Monster.MaxLife;
                var preservedShield = freshResource.ShieldPool.Capture(preservedAreaTime);
                var preservedMaximumShield = freshResource.MaximumShield;
                var preservedRegeneration = freshResource.Regeneration;
                var preservedLastStep = freshResource.LastStep;
                var preservedBusyUntil = freshResource.Monster.BusyUntil;
                var preservedFlinchUntil = freshResource.Monster.FlinchUntil;
                var preservedStunImmuneUntil = freshResource.Monster.StunImmuneUntil;
                var preservedActionGeneration = freshResource.Monster.ActionGeneration;
                var preservedSwingGeneration = freshResource.Monster.SwingGeneration;
                var preservedSwingOrderedAt = freshResource.Monster.SwingOrderedAt;
                var preservedReceivedActionFactor = freshResource.Monster.ReceivedActionFactor;
                var preservedReceivedActionUntil = freshResource.Monster.ReceivedActionUntil;
                var preservedReceivedActionFloor = freshResource.Monster.ReceivedActionFloor;
                var preservedRecipientDebuffRate = freshResource.Monster.RecipientDebuffRate;
                var preservedPending = freshResource.Pending;
                var preservedAbilities = freshResource.Abilities;
                var preservedMods = freshResource.Monster.Mods;
                var preservedGolemPlayer = new Dictionary<uint, int>(freshResource.Player);
                var preservedNextMove = freshResource.NextMove;
                var preservedNextOrb = freshResource.NextOrb;
                var preservedCombo = freshResource.Combo;
                var preservedInputHash = freshResource.InputHash;
                var preservedProjectileRuntime = resource.ProjectileRuntime;
                var preservedCharges = freshResource.Charges.ToDictionary(pair => pair.Key,
                    pair => new Queue<double>(pair.Value));
                // Labelled diagnostic: force the existing minion critical stat through the public raw player-stat path.
                // The real source refresh must preserve the zero chance.
                var minionCriticalChance = Program.Stats.Key("minion_critical_strike_chance_+%")!.Value;
                resource.PlayerStats[minionCriticalChance] = -100;
                RecalculateResource();
                resourceSchedule.At = 20.26;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                var previousOutgoingAbilities = freshResource.Abilities;
                var previousOutgoingMods = freshResource.Monster.Mods;
                var projectileAbility = Assert.Single(previousOutgoingAbilities,
                    ability => ability.Projectile is { Lobbed: false } && ability.Attack.Damage.MaxTotal > 0);
                Assert.Equal(0, Game.GolemCriticalChance(projectileAbility, previousOutgoingMods));
                var outgoingTargetId = 0x0A22D3D0u;
                var outgoingAt = freshResource.Monster.Walk.Position(20.26);
                var outgoingTarget = new SpawnedMonster("Fleshcrafter impact target",
                    new(outgoingAt.X + 15, outgoingAt.Y, 0), 100, 8, 1_000_000)
                {
                    Hostile = true,
                    Mods = MonsterModSet.None with
                    {
                        Effects = new(new Dictionary<string, int>
                        {
                            ["base_fire_damage_resistance_%"] = 25,
                            ["base_chaos_damage_resistance_%"] = 25,
                            ["base_additional_physical_damage_reduction_%"] = 25,
                        }),
                    },
                };
                Assert.DoesNotContain(outgoingTargetId, resource.Monsters.Keys);
                resource.Monsters[outgoingTargetId] = outgoingTarget;
                var farTargetId = 0x0A22D3D2u;
                var farTarget = new SpawnedMonster("Fleshcrafter far impact target",
                    new(outgoingAt.X + 140, outgoingAt.Y, 0), 100, 8, 1_000_000)
                {
                    Hostile = true,
                    Mods = outgoingTarget.Mods,
                };
                Assert.DoesNotContain(farTargetId, resource.Monsters.Keys);
                resource.Monsters[farTargetId] = farTarget;
                Game.ProjectileSession? impactSession = null;
                try
                {
                    GolemState.Ability GolemAbility(DamageType type) => projectileAbility with
                    {
                        Attack = projectileAbility.Attack with
                        {
                            Spell = true,
                            Damage = DamagePacket.Of(type, 400, 400),
                        },
                    };
                    GolemState.Ability fixedAbility = GolemAbility(DamageType.Fire);
                    freshResource.Abilities = [fixedAbility];
                    uint LifeLoss(GolemState.Ability ability)
                    {
                        freshResource.Abilities = [ability];
                        outgoingTarget.Life = outgoingTarget.MaxLife;
                        uint before = outgoingTarget.Life;
                        Assert.True(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource,
                            freshResource, ability, outgoingTargetId, outgoingTarget, 20.27));
                        return before - outgoingTarget.Life;
                    }

                    Assert.True(freshResource.MaximumShield > 1);
                    freshResource.ShieldPool.SetMaximum(freshResource.MaximumShield, refill: true);
                    freshResource.ShieldPool.Damage((int)freshResource.MaximumShield - 1, bypass: false, now: 20.26);
                    Assert.Equal(1u, freshResource.Shield);
                    Assert.Equal(400u, LifeLoss(fixedAbility));
                    freshResource.ShieldPool.Damage(1, bypass: false, now: 20.27);
                    Assert.Equal(0u, freshResource.Shield);
                    Assert.Equal(300u, LifeLoss(fixedAbility));
                    freshResource.ShieldPool.SetMaximum(freshResource.MaximumShield, refill: true);
                    freshResource.ShieldPool.Damage((int)freshResource.MaximumShield - 1, bypass: false, now: 20.28);
                    Assert.Equal(300u, LifeLoss(GolemAbility(DamageType.Physical)));
                    Assert.Equal(1u, freshResource.Shield);
                    Assert.Equal(300u, LifeLoss(GolemAbility(DamageType.Chaos)));

                    // The equipment source changes without waiting for the .5 s roster refresh. GolemHitLocked must
                    // sample a fresh owner and the actual current source at contact, not the cached Fleshcrafter effect.
                    var ignoreKey = Program.Stats.Key(
                        "minion_hits_ignore_enemy_elemental_resistances_while_has_energy_shield")!.Value;
                    Assert.NotEqual(0, freshResource.Player.GetValueOrDefault(ignoreKey));
                    freshResource.Abilities = [projectileAbility];
                    freshResource.Monster.Mods = previousOutgoingMods;
                    freshResource.ShieldPool.SetMaximum(freshResource.MaximumShield, refill: true);
                    Assert.NotEqual(0u, freshResource.Shield);
                    resourceInventory.Remove(PlayerInventories.BodyArmour, armourSlot.Id);
                    RecalculateResource();
                    Assert.NotEqual(0, freshResource.Player.GetValueOrDefault(ignoreKey));
                    outgoingTarget.Life = outgoingTarget.MaxLife;
                    uint normalBefore = outgoingTarget.Life;
                    Assert.True(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource, freshResource,
                        projectileAbility, outgoingTargetId, outgoingTarget, 20.29));
                    int normalLost = checked((int)(normalBefore - outgoingTarget.Life));
                    Assert.Equal(0, freshResource.Player.GetValueOrDefault(ignoreKey));
                    var normalAbility = Assert.Single(freshResource.Abilities,
                        ability => ability.Effect == projectileAbility.Effect);
                    Assert.Equal(0, Game.GolemCriticalChance(normalAbility, freshResource.Monster.Mods));
                    int NormalComponent(DamageType type, double value) => Game.MonsterTakesMitigated(outgoingTarget,
                        DamageModel.Mitigate(DamagePacket.Of(type, value, value),
                            damage => Game.MonsterHitResistance(outgoingTarget, damage, default)), spell: true);
                    Assert.InRange(normalLost,
                        NormalComponent(DamageType.Fire, normalAbility.Attack.Damage[DamageType.Fire].Min),
                        NormalComponent(DamageType.Fire, normalAbility.Attack.Damage[DamageType.Fire].Max));

                    resourceInventory.Add(PlayerInventories.BodyArmour, 0, 0, preservedArmourItem);
                    armourSlot = resourceInventory.Items(PlayerInventories.BodyArmour).Single();
                    RecalculateResource();
                    Assert.Equal(0, freshResource.Player.GetValueOrDefault(ignoreKey));
                    uint shieldBeforeSourceOnHit = freshResource.Shield;
                    outgoingTarget.Life = outgoingTarget.MaxLife;
                    uint ignoredBefore = outgoingTarget.Life;
                    Assert.True(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource, freshResource,
                        projectileAbility, outgoingTargetId, outgoingTarget, 20.3));
                    Assert.NotEqual(0, freshResource.Player.GetValueOrDefault(ignoreKey));
                    Assert.True(freshResource.MaximumShield > 0);
                    Assert.Equal(shieldBeforeSourceOnHit, freshResource.Shield);
                    int ignoredLost = checked((int)(ignoredBefore - outgoingTarget.Life));
                    var ignoredAbility = Assert.Single(freshResource.Abilities,
                        ability => ability.Effect == projectileAbility.Effect);
                    Assert.Equal(0, Game.GolemCriticalChance(ignoredAbility, freshResource.Monster.Mods));
                    Assert.InRange(ignoredLost,
                        (int)Math.Round(ignoredAbility.Attack.Damage[DamageType.Fire].Min),
                        (int)Math.Round(ignoredAbility.Attack.Damage[DamageType.Fire].Max));

                    // A vanished gem source is not kept alive by the roster. Dead and replaced actors likewise cannot
                    // deliver a new hit.
                    resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with { Sockets = [] });
                    RecalculateResource();
                    uint missingSourceLife = outgoingTarget.Life;
                    Assert.False(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource, freshResource,
                        projectileAbility, outgoingTargetId, outgoingTarget, 20.31));
                    Assert.Equal(missingSourceLife, outgoingTarget.Life);
                    resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                        item => item with { Sockets = preservedWeaponItem.Sockets });
                    RecalculateResource();
                    resourceSource = Assert.Single(Game.GemSkillEffects(resource),
                        grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonFlameGolem");
                    freshResource.Source = resourceSource.Id;

                    freshResource.Monster.Dead = true;
                    Assert.False(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource, freshResource,
                        projectileAbility, outgoingTargetId, outgoingTarget, 20.32));
                    freshResource.Monster.Dead = false;
                    freshResource.Monster.Life = freshResource.Monster.MaxLife;
                    var originalRoster = resource.Golems.All[freshResource.Id];
                    var replacementRoster = new GolemState.Golem(freshResource.Id, freshResource.Monster,
                        freshResource.Definition, freshResource.Source);
                    resource.Golems.All[freshResource.Id] = replacementRoster;
                    Assert.False(Game.GolemHitLocked("test", wire.Stream, wire.Ciphers, resource, freshResource,
                        projectileAbility, outgoingTargetId, outgoingTarget, 20.33));
                    resource.Golems.All[freshResource.Id] = originalRoster;

                    // Actual projectile route. Release with ES positive, deplete that ES through real incoming hits,
                    // then contact through ObjectProjectileOffense. A second flight releases at ES zero and recovers
                    // through the production shield kernel before contact. No direct GolemHitLocked contact is used here.
                    var fixedProjectile = projectileAbility with
                    {
                        Attack = projectileAbility.Attack with { Spell = true,
                            Damage = DamagePacket.Of(DamageType.Fire, 400, 400) },
                        Projectile = projectileAbility.Projectile! with { Count = 1, SpreadRadians = 0 },
                    };
                    impactSession = new Game.ProjectileSession("test", wire.Stream, wire.Ciphers, resource);
                    resource.ProjectileRuntime = impactSession;
                    freshResource.Abilities = [fixedProjectile];
                    freshResource.Monster.BusyUntil = 21;
                    freshResource.Charges.Clear();
                    freshResource.ShieldPool.SetMaximum(freshResource.MaximumShield, refill: true);
                    resource.IncomingHitRandom = new LowestRandom();
                    resourceSchedule.At = 21;
                    string Number(double value) => double.IsPositiveInfinity(value) ? "+inf"
                        : double.IsNegativeInfinity(value) ? "-inf"
                        : double.IsNaN(value) ? "nan" : value.ToString("0.#####");
                    string Position((int X, int Y) value) => $"({value.X},{value.Y})";
                    double Distance((int X, int Y) a, (int X, int Y) b) =>
                        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    double ProjectileDistance(GameServer.Core.Projectiles.ProjectileVector a, (int X, int Y) b) =>
                        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                    double ContactReach(GameServer.Core.Projectiles.ProjectileEntity projectile,
                        SpawnedMonster target) => projectile.Profile.ContactCells is int cells
                        ? cells + LivingCollision.Size(target) + .5
                        // Public Actor creation uses Radius=0 and exposes Size separately; circle contact adds no radius.
                        : projectile.Profile.Radius;
                    string DamageText(DamagePacket damage) => string.Join("+", damage.Components.Select(component =>
                        $"{component.Type}:{Number(component.Range.Min)}-{Number(component.Range.Max)}"));
                    string AbilityText(GolemState.Golem golem, GolemState.Ability ability)
                    {
                        return $"{ability.Id}[effect={ability.Effect},skill={ability.Skill}," +
                            $"range={Number(ability.Attack.Range)},damage={DamageText(ability.Attack.Damage)}," +
                            $"contact={Number(ability.Contact)}," +
                            $"projectile={ability.Projectile?.Metadata ?? "none"}]";
                    }
                    string PendingText(GolemState.Golem golem, GolemState.Action? pending) => pending is null
                        ? "null" : $"{AbilityText(golem, pending.Ability)},target=0x{pending.Target:x8}," +
                            $"aim={Position(pending.Aim)},contact={Number(pending.ContactAt)},at={Number(pending.At)}," +
                            $"generation={pending.Generation},started={pending.Started}";
                    string TargetText(string role, uint id, SpawnedMonster target, double now,
                        (int X, int Y) golemAt, (int X, int Y) ownerAt)
                    {
                        var at = target.Walk.Position(now);
                        string effects = string.Join(",", target.Mods.Effects.Stats.OrderBy(pair => pair.Key,
                            StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
                        return $"{role}=0x{id:x8}[label={target.Label},pos21={Position(at)}," +
                            $"Hostile={target.Hostile},Dead={target.Dead},Life={target.Life},MaxLife={target.MaxLife}," +
                            $"Asleep={target.Asleep},connAsleep={resource.AsleepMonsters.Contains(id)}," +
                            $"Invulnerable={target.Invulnerable},BusyUntil={Number(target.BusyUntil)}," +
                            $"distFromG={Number(Distance(at, golemAt))},distFromOwner={Number(Distance(at, ownerAt))}," +
                            $"receivedActionFactor={Number(target.ReceivedActionFactor)}," +
                            $"receivedActionUntil={Number(target.ReceivedActionUntil)}," +
                            $"receivedActionFloor={target.ReceivedActionFloor}," +
                            $"ailmentActionFactor21={Number(target.Ailments.ActionFactor(now))}," +
                            $"monsterActionFactor21={Number(Game.MonsterActionFactor(target, now))}," +
                            $"effects=[{effects}]]";
                    }
                    string PublicSnapshot(string phase, GolemState.Golem golem, double now)
                    {
                        var golemAt = golem.Monster.Walk.Position(now);
                        var ownerAt = resource.Walk.Position(now);
                        var supported = Game.SupportedSkill(resource, golem.Source);
                        bool rosterSame = ReferenceEquals(resource.Golems.All.GetValueOrDefault(golem.Id), golem);
                        bool monstersSame = ReferenceEquals(resource.Monsters.GetValueOrDefault(golem.Id), golem.Monster);
                        string abilities = string.Join(" | ", golem.Abilities.Select(ability => AbilityText(golem, ability)));
                        return $"{phase} at={Number(now)},schedule.At={Number(resourceSchedule.At)}\n" +
                            $"record=0x{golem.Id:x8},definition=0x{golem.Definition.Effect}:{golem.Definition.Id}," +
                            $"kind={golem.Definition.Kind},variety={golem.Definition.Variety},source=0x{golem.Source:x8}," +
                            $"rosterSame={rosterSame},monstersSame={monstersSame}," +
                            $"minionsOwns={resource.Minions.Owns(golem.Id, golem.Monster)},dead={golem.Monster.Dead}," +
                            $"life={golem.Monster.Life}/{golem.Monster.MaxLife},shield={golem.Shield}/{golem.MaximumShield}," +
                            $"gemLevel={golem.GemLevel},quality={golem.Quality}," +
                            $"supportedGem=(effect={supported.Effect},level={supported.Level},quality={supported.Quality}," +
                            $"requirements={supported.RequirementsMet},supports={supported.Supports.Count})," +
                            $"closed={resource.Closed},leavingArea={resource.LeavingArea},paused={resource.Paused}," +
                            $"currentGameWork={Game.CurrentGameWork},inTown={Game.InTown(resource)}," +
                            $"clockElapsed={Number(resource.Clock.Elapsed.TotalSeconds)},areaTime={Number(Game.AreaTime(resource))}," +
                            $"connWalkPosition21={Position(ownerAt)},gWalkPosition21={Position(golemAt)}," +
                            $"gAggroRange={golem.Monster.AggroRange}," +
                            $"busyUntil={Number(golem.Monster.BusyUntil)},flinchUntil={Number(golem.Monster.FlinchUntil)}," +
                            $"actionGeneration={golem.Monster.ActionGeneration}," +
                            $"monsterActionFactor21={Number(Game.MonsterActionFactor(golem.Monster, now))}," +
                            $"receivedActionFactor={Number(golem.Monster.ReceivedActionFactor)}," +
                            $"receivedActionUntil={Number(golem.Monster.ReceivedActionUntil)}," +
                            $"receivedActionFloor={golem.Monster.ReceivedActionFloor}," +
                            $"ailmentActionFactor21={Number(golem.Monster.Ailments.ActionFactor(now))}," +
                            $"target=0x{golem.Target:x8}," +
                            $"pending={PendingText(golem, golem.Pending)}\n" +
                            $"targets=[{TargetText("first", outgoingTargetId, outgoingTarget, now, golemAt, ownerAt)}; " +
                            $"{TargetText("far", farTargetId, farTarget, now, golemAt, ownerAt)}]\n" +
                            $"abilities=[{abilities}]";
                    }
                    var golemAt21 = freshResource.Monster.Walk.Position(resourceSchedule.At);
                    var ownerAt21 = resource.Walk.Position(resourceSchedule.At);
                    outgoingTarget.Walk = new PlayerWalk(golemAt21.X + 15, golemAt21.Y, 0);
                    farTarget.Walk = new PlayerWalk(golemAt21.X + 140, golemAt21.Y, 0);
                    outgoingTarget.Life = outgoingTarget.MaxLife;
                    outgoingTarget.Burn = 0;
                    outgoingTarget.Regen = 0;
                    outgoingTarget.Ignites.Clear();
                    outgoingTarget.Bleeds.Clear();
                    outgoingTarget.Poisons.Clear();
                    double firstTargetDistance = Distance(outgoingTarget.Walk.Position(resourceSchedule.At), golemAt21);
                    Assert.True(firstTargetDistance <= fixedProjectile.Attack.Range + LivingCollision.Size(outgoingTarget),
                        $"first target {Position(outgoingTarget.Walk.Position(resourceSchedule.At))} is " +
                        $"{Number(firstTargetDistance)} from golem {Position(golemAt21)}; ability range is " +
                        $"{Number(fixedProjectile.Attack.Range)}, target body size {LivingCollision.Size(outgoingTarget)}");
                    double firstTargetOwnerDistance = Distance(outgoingTarget.Walk.Position(resourceSchedule.At), ownerAt21);
                    Assert.True(firstTargetOwnerDistance <= 100,
                        $"first target owner distance {Number(firstTargetOwnerDistance)} from owner {Position(ownerAt21)}");
                    var before21 = PublicSnapshot("BEFORE", freshResource, resourceSchedule.At);
                    await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                    var after21 = PublicSnapshot("AFTER", freshResource, resourceSchedule.At);
                    Assert.True(freshResource.Pending is not null,
                        $"first Pending is null after GolemsTick at 21\n{before21}\n{after21}");
                    Assert.Equal(outgoingTargetId, freshResource.Target);
                    var firstPending = freshResource.Pending!;
                    Assert.Equal(fixedProjectile.Effect, firstPending.Ability.Effect);
                    Assert.True(firstPending.Started);
                    double firstContact = firstPending.At;
                    resourceSchedule.At = firstContact;
                    await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                    Assert.Equal(outgoingTarget.MaxLife, outgoingTarget.Life);
                    double firstDepletionAt = firstContact + .101;
                    impactSession.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(firstDepletionAt));
                    var firstProjectile = Assert.Single(impactSession.Runtime.Projectiles);
                    Assert.True(freshResource.Shield > 0);
                    Assert.Equal(outgoingTarget.MaxLife, outgoingTarget.Life);
                    var incomingSource = new SpawnedMonster("actual ES depletion", new(100, 100, 1),
                        100, 8, 1_000_000);
                    Assert.Empty(incomingSource.Effects.Stats);
                    Assert.Equal(0, incomingSource.Effects.CriticalChance);
                    resourceSchedule.At = firstDepletionAt;
                    var firstPhysicalResistance = Game.MonsterHitResistance(
                        freshResource.Monster, DamageType.Physical, default);
                    int firstPlayerArmour = Program.Stats.Key("minion_physical_damage_reduction_rating")
                        is uint firstDepletionPlayerArmourKey
                        ? freshResource.Player.GetValueOrDefault(firstDepletionPlayerArmourKey) : 0;
                    int firstGolemArmour = freshResource.Definition.IsGolem
                        && Program.Stats.Key("golem_physical_damage_reduction_rating")
                            is uint firstDepletionGolemArmourKey
                        ? freshResource.Player.GetValueOrDefault(firstDepletionGolemArmourKey) : 0;
                    int firstEffectArmour = freshResource.Monster.Effects["base_physical_damage_reduction_rating"];
                    int firstArmour = Math.Max(0, firstPlayerArmour + firstGolemArmour + firstEffectArmour);
                    int firstPhysicalReduction = freshResource.Monster.Effects.PhysicalReduction;
                    double firstDamageTaken = freshResource.Monster.Effects.DamageTaken(spell: true);
                    string firstPhysicalState = $"at={Number(firstDepletionAt)},shield={freshResource.Shield}," +
                        $"resistance={firstPhysicalResistance.Value}/{firstPhysicalResistance.Maximum}," +
                        $"penetration={firstPhysicalResistance.Penetration},armour={firstArmour}" +
                        $"(player={firstPlayerArmour},golem={firstGolemArmour},effects={firstEffectArmour})," +
                        $"physicalReduction={firstPhysicalReduction},damageTaken={Number(firstDamageTaken)}," +
                        $"alwaysCrit={incomingSource.Effects["always_crit"]}," +
                        $"criticalChance={incomingSource.Effects.CriticalChance}," +
                        $"areaDamageTaken={freshResource.Monster.Effects["damage_taken_+%_from_area_hits"]}";
                    Assert.True(firstPhysicalResistance.Value == 0 && firstPhysicalResistance.Penetration == 0
                        && firstArmour == 0 && firstPhysicalReduction == 0 && firstDamageTaken == 1,
                        $"first physical depletion preconditions are not orthogonal: {firstPhysicalState}");
                    uint firstRawDamage = freshResource.Shield;
                    uint firstLifeBeforeHit = freshResource.Monster.Life;
                    var firstPhysicalHit = new MonsterAttack(0x4000, 1, 100,
                        DamagePacket.Of(DamageType.Physical, checked((int)firstRawDamage),
                            checked((int)firstRawDamage)))
                    { Spell = true };
                    Assert.True(Game.GolemTakeHitLocked(resource, freshResource, 0x0A22D3D1,
                        incomingSource, firstPhysicalHit, firstDepletionAt), firstPhysicalState);
                    Assert.Equal(0u, freshResource.Shield);
                    Assert.Equal(firstLifeBeforeHit, freshResource.Monster.Life);
                    Assert.Same(firstProjectile, Assert.Single(impactSession.Runtime.Projectiles));
                    Assert.Equal(outgoingTarget.MaxLife, outgoingTarget.Life);
                    uint normalProjectileBefore = outgoingTarget.Life;
                    double firstRuntimeAt = impactSession.Runtime.Now /
                        (double)GameServer.Core.Projectiles.ProjectileRuntime.QuantaPerSecond;
                    double firstRemaining = ProjectileDistance(firstProjectile.Position,
                        outgoingTarget.Walk.Position(firstRuntimeAt));
                    double firstStop = firstRuntimeAt + Math.Max(0,
                        firstRemaining - ContactReach(firstProjectile, outgoingTarget)) / firstProjectile.Speed + .001;
                    Assert.True(firstStop >= resourceSchedule.At,
                        $"first runtime stop {Number(firstStop)} precedes area time {Number(resourceSchedule.At)}");
                    resourceSchedule.At = firstStop;
                    impactSession.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(firstStop));
                    uint firstProjectileLoss = normalProjectileBefore - outgoingTarget.Life;
                    double firstRuntimeNow = impactSession.Runtime.Now /
                        (double)GameServer.Core.Projectiles.ProjectileRuntime.QuantaPerSecond;
                    string firstConnTarget = resource.Monsters.GetValueOrDefault(outgoingTargetId)?.Label ?? "none";
                    Assert.True(firstProjectileLoss == 300u,
                        $"first actual projectile loss={firstProjectileLoss}, expected=300, " +
                        $"firstContact={Number(firstContact)}, stop={Number(firstStop)}, " +
                        $"runtimeNow={Number(firstRuntimeNow)}, " +
                        $"projectiles={impactSession.Runtime.Projectiles.Count},shield={freshResource.Shield}, " +
                        $"targetLife={outgoingTarget.Life}/{outgoingTarget.MaxLife},targetDead={outgoingTarget.Dead}," +
                        $"hostile={outgoingTarget.Hostile},invulnerable={outgoingTarget.Invulnerable}," +
                        $"golemDead={freshResource.Monster.Dead},golemLife={freshResource.Monster.Life}," +
                        $"flinch={Number(freshResource.Monster.FlinchUntil)}," +
                        $"actionGeneration={freshResource.Monster.ActionGeneration}," +
                        $"pending={PendingText(freshResource, freshResource.Pending)}, " +
                        $"source={freshResource.Source},connTarget={firstConnTarget}");

                    impactSession.Invalidate();
                    await impactSession.OutputCompleted;
                    impactSession = null;
                    resource.ProjectileRuntime = null;

                    // The far diagnostic action contacts after enough real flight time for the production shield
                    // kernel to recharge. First recover, then deplete through one real Physical spell hit immediately
                    // before the prepared release, so release has ES zero and contact has ES positive.
                    double recoveryStart = firstContact + 20;
                    double secondDepletionAt = firstContact + 21.1;
                    resourceSchedule.At = recoveryStart;
                    freshResource.ShieldPool.Advance(recoveryStart, secondDepletionAt,
                        name => freshResource.Monster.Mods.Effects[name], alive: true);
                    Assert.True(freshResource.Shield > 0);
                    resourceSchedule.At = secondDepletionAt;
                    var secondPhysicalResistance = Game.MonsterHitResistance(
                        freshResource.Monster, DamageType.Physical, default);
                    int secondPlayerArmour = Program.Stats.Key("minion_physical_damage_reduction_rating")
                        is uint secondDepletionPlayerArmourKey
                        ? freshResource.Player.GetValueOrDefault(secondDepletionPlayerArmourKey) : 0;
                    int secondGolemArmour = freshResource.Definition.IsGolem
                        && Program.Stats.Key("golem_physical_damage_reduction_rating")
                            is uint secondDepletionGolemArmourKey
                        ? freshResource.Player.GetValueOrDefault(secondDepletionGolemArmourKey) : 0;
                    int secondEffectArmour = freshResource.Monster.Effects["base_physical_damage_reduction_rating"];
                    int secondArmour = Math.Max(0, secondPlayerArmour + secondGolemArmour + secondEffectArmour);
                    int secondPhysicalReduction = freshResource.Monster.Effects.PhysicalReduction;
                    double secondDamageTaken = freshResource.Monster.Effects.DamageTaken(spell: true);
                    string secondPhysicalState = $"at={Number(secondDepletionAt)},shield={freshResource.Shield}," +
                        $"resistance={secondPhysicalResistance.Value}/{secondPhysicalResistance.Maximum}," +
                        $"penetration={secondPhysicalResistance.Penetration},armour={secondArmour}" +
                        $"(player={secondPlayerArmour},golem={secondGolemArmour},effects={secondEffectArmour})," +
                        $"physicalReduction={secondPhysicalReduction},damageTaken={Number(secondDamageTaken)}," +
                        $"alwaysCrit={incomingSource.Effects["always_crit"]}," +
                        $"criticalChance={incomingSource.Effects.CriticalChance}," +
                        $"areaDamageTaken={freshResource.Monster.Effects["damage_taken_+%_from_area_hits"]}";
                    Assert.True(secondPhysicalResistance.Value == 0 && secondPhysicalResistance.Penetration == 0
                        && secondArmour == 0 && secondPhysicalReduction == 0 && secondDamageTaken == 1,
                        $"second physical depletion preconditions are not orthogonal: {secondPhysicalState}");
                    uint secondRawDamage = freshResource.Shield;
                    uint secondLifeBeforeHit = freshResource.Monster.Life;
                    var secondPhysicalHit = new MonsterAttack(0x4000, 1, 100,
                        DamagePacket.Of(DamageType.Physical, checked((int)secondRawDamage),
                            checked((int)secondRawDamage)))
                    { Spell = true };
                    Assert.True(Game.GolemTakeHitLocked(resource, freshResource, 0x0A22D3D3,
                        incomingSource, secondPhysicalHit, secondDepletionAt), secondPhysicalState);
                    Assert.Equal(0u, freshResource.Shield);
                    Assert.Equal(secondLifeBeforeHit, freshResource.Monster.Life);
                    freshResource.Pending = null;
                    freshResource.Abilities = [fixedProjectile];
                    freshResource.Charges.Clear();
                    impactSession = new Game.ProjectileSession("test", wire.Stream, wire.Ciphers, resource);
                    resource.ProjectileRuntime = impactSession;
                    double secondStart = firstContact + 21.2;
                    resourceSchedule.At = secondStart;
                    var releasePosition = freshResource.Monster.Walk.Position(secondStart);
                    farTarget = new SpawnedMonster("Fleshcrafter far impact target",
                        new PlayerWalk(releasePosition.X + 140, releasePosition.Y, 0), 100, 8, 1_000_000)
                    {
                        Hostile = true,
                        Mods = outgoingTarget.Mods,
                    };
                    resource.Monsters[farTargetId] = farTarget;
                    var secondAction = new GolemState.Action(fixedProjectile, farTargetId, farTarget,
                        farTarget.Walk.Position(secondStart), secondStart, freshResource.Monster.ActionGeneration);
                    Assert.Equal(releasePosition.X + 140, farTarget.Walk.Position(secondStart).X);
                    Assert.Equal(releasePosition.Y, farTarget.Walk.Position(secondStart).Y);
                    Game.GolemAbilityContactLocked("test", wire.Stream, wire.Ciphers, resource,
                        freshResource, secondAction, secondStart);
                    double secondRelease = secondStart + .1;
                    impactSession.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(secondRelease));
                    Assert.Equal(0u, freshResource.Shield);
                    var secondProjectile = Assert.Single(impactSession.Runtime.Projectiles);
                    uint ignoredProjectileBefore = farTarget.Life;
                    Assert.Equal(farTarget.MaxLife, ignoredProjectileBefore);
                    double naturalSpeed = ProjectileSpeed.CellsPerSecond(fixedProjectile.Projectile!.Speed,
                        _ => 0, _ => 0);
                    Assert.Equal(69d, naturalSpeed);
                    double releaseCenterDistance = Distance(farTarget.Walk.Position(secondRelease), releasePosition);
                    double naturalFlight = releaseCenterDistance / naturalSpeed;
                    double rechargeDelay = EnergyShield.Delay(
                        name => freshResource.Monster.Mods.Effects[name]);
                    double recoveryAt = secondRelease + rechargeDelay + .1;
                    Assert.True(recoveryAt < secondRelease + naturalFlight,
                        $"recovery {recoveryAt:0.###} must precede natural contact {secondRelease + naturalFlight:0.###}");
                    double secondRemaining = ProjectileDistance(secondProjectile.Position,
                        farTarget.Walk.Position(secondRelease));
                    double secondReach = ContactReach(secondProjectile, farTarget);
                    double earliestContact = secondRelease + Math.Max(0, secondRemaining - secondReach) /
                        secondProjectile.Speed;
                    Assert.True(recoveryAt < earliestContact,
                        $"recovery {recoveryAt:0.###} must precede earliest contact {earliestContact:0.###}; " +
                        $"release {Position(releasePosition)}, projectile {secondProjectile.Position}, " +
                        $"target {Position(farTarget.Walk.Position(secondRelease))}, remaining {secondRemaining:0.###}, " +
                        $"reach {secondReach:0.###}, speed {secondProjectile.Speed:0.###}");
                    resourceSchedule.At = recoveryAt;
                    impactSession.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(recoveryAt));
                    Assert.Same(secondProjectile, Assert.Single(impactSession.Runtime.Projectiles));
                    Assert.Equal(farTarget.MaxLife, farTarget.Life);
                    freshResource.ShieldPool.Advance(secondRelease, recoveryAt,
                        name => freshResource.Monster.Mods.Effects[name], alive: true);
                    Assert.True(freshResource.Shield > 0);
                    Assert.InRange(freshResource.Shield, 1u, freshResource.MaximumShield);
                    double contactStop = earliestContact + .001;
                    Assert.True(contactStop >= resourceSchedule.At,
                        $"contact stop {contactStop:0.###} precedes area time {resourceSchedule.At:0.###}");
                    resourceSchedule.At = contactStop;
                    impactSession.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(contactStop));
                    Assert.Equal(400u, ignoredProjectileBefore - farTarget.Life);
                }
                finally
                {
                    if (impactSession is not null)
                    {
                        impactSession.Invalidate();
                        await impactSession.OutputCompleted;
                    }
                    resource.ProjectileRuntime = preservedProjectileRuntime;
                    resource.Monsters.Remove(outgoingTargetId);
                    resource.Monsters.Remove(farTargetId);
                    void RestoreEquipment(byte row, Item item)
                    {
                        var current = resourceInventory.Items(row).ToArray();
                        if (current.Length == 1 && current[0].Item == item) return;
                        foreach (var existing in current) resourceInventory.Remove(row, existing.Id);
                        resourceInventory.Add(row, 0, 0, item);
                    }
                    RestoreEquipment(PlayerInventories.BodyArmour, preservedArmourItem);
                    RestoreEquipment(PlayerInventories.Weapon1, preservedWeaponItem);
                    resource.PlayerStats.Clear();
                    foreach (var (key, value) in preservedPlayerStats) resource.PlayerStats[key] = value;
                    RecalculateResource();
                    freshResource.Abilities = preservedAbilities;
                    freshResource.Monster.Mods = preservedMods;
                    freshResource.Player = preservedGolemPlayer;
                    freshResource.Monster.Walk = preservedGolemWalk;
                    freshResource.Target = preservedGolemTarget;
                    freshResource.Monster.Aggro = preservedAggro;
                    freshResource.Monster.LastTarget = preservedLastTarget;
                    freshResource.Monster.Dead = false;
                    freshResource.Monster.MaxLife = preservedMaximumLife;
                    freshResource.Monster.Life = preservedLife;
                    freshResource.ShieldPool.SetMaximum(preservedMaximumShield);
                    freshResource.ShieldPool.Restore(preservedShield, preservedAreaTime);
                    freshResource.Regeneration = preservedRegeneration;
                    freshResource.LastStep = preservedLastStep;
                    freshResource.Monster.BusyUntil = preservedBusyUntil;
                    freshResource.Monster.FlinchUntil = preservedFlinchUntil;
                    freshResource.Monster.StunImmuneUntil = preservedStunImmuneUntil;
                    freshResource.Monster.ActionGeneration = preservedActionGeneration;
                    freshResource.Monster.SwingGeneration = preservedSwingGeneration;
                    freshResource.Monster.SwingOrderedAt = preservedSwingOrderedAt;
                    freshResource.Monster.ReceivedActionFactor = preservedReceivedActionFactor;
                    freshResource.Monster.ReceivedActionUntil = preservedReceivedActionUntil;
                    freshResource.Monster.ReceivedActionFloor = preservedReceivedActionFloor;
                    freshResource.Monster.RecipientDebuffRate = preservedRecipientDebuffRate;
                    freshResource.Pending = preservedPending;
                    freshResource.NextMove = preservedNextMove;
                    freshResource.NextOrb = preservedNextOrb;
                    freshResource.Combo = preservedCombo;
                    freshResource.InputHash = preservedInputHash;
                    freshResource.Charges.Clear();
                    foreach (var (effect, charges) in preservedCharges) freshResource.Charges[effect] = charges;
                    resource.IncomingHitRandom = preservedIncomingRandom;
                    resourceSchedule.At = preservedAreaTime;
                }

                resource.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 2;
                RecalculateResource();
                resourceSchedule.At = 20.3;
                uint capPeerId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100));
                var capPeer = resource.Golems.All[capPeerId];
                resourceSchedule.At = 20.4;
                uint exactNewerId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: capPeer);
                Assert.NotEqual(0u, exactNewerId);
                Assert.Equal(2, resource.Golems.All.Count);
                Assert.Same(freshResource, resource.Golems.All[freshResource.Id]);
                Assert.DoesNotContain(capPeer, resource.Golems.All.Values);
                Assert.Same(resource.Golems.All[exactNewerId], resource.Golems.All.Values.Single(g => g.Id == exactNewerId));
                var exactNewer = resource.Golems.All[exactNewerId];
                resource.Monsters.Remove(exactNewerId);
                resource.Minions.Remove(exactNewerId);
                resource.MinionOrigins.TryRemove(exactNewerId, out _);
                resource.Golems.All.Remove(exactNewerId);
                resource.PlayerStats[Program.Stats.Key("number_of_golems_allowed")!.Value] = 1;
                RecalculateResource();
                resource.Golems.RefreshAt = 0;

                resourceSchedule.At = 20.25;
                freshResource.Monster.Life = freshResource.Monster.MaxLife - 137;
                freshResource.ShieldPool.Damage(Math.Max(1, (int)freshResource.Shield / 3), bypass: false, now: 20.25);
                freshResource.Regeneration = .375;
                uint savedLife = freshResource.Monster.Life;
                uint savedShield = freshResource.Shield;
                uint savedMaximumLife = freshResource.Monster.MaxLife;
                uint savedMaximumShield = freshResource.MaximumShield;
                Assert.InRange(savedLife, 1u, savedMaximumLife - 1);
                Assert.InRange(savedShield, 1u, savedMaximumShield - 1);
                Game.SaveGolems(resource);
                string currentResourceJson = CharacterSaves.ModuleData(resourceCharacter, "Game.Golems")!;
                var currentResourceEntries = JsonSerializer.Deserialize<GolemState.Saved[]>(currentResourceJson)!;
                var currentSaved = Assert.Single(currentResourceEntries,
                    entry => entry.Effect == resourceDefinition.Id && entry.ReviveSeconds == 0);
                Assert.NotNull(currentSaved.Resources);
                Assert.Equal(savedLife, currentSaved.Resources!.Life);
                Assert.Equal(.375, currentSaved.Resources.LifeRegeneration);
                Assert.Equal(savedShield, currentSaved.Resources.EnergyShield.Current);
                Assert.Equal(0d, currentSaved.Resources.EnergyShield.InterruptedSecondsAgo!.Value);

                resourceSchedule.At = 1000;
                var restoredResource = NewResourceClient("integer-resource-restored", 1000);
                var restoredResourcePackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, restoredResource, 1000));
                var restoredResourceActor = Assert.Single(restoredResource.Golems.All).Value;
                uint restoredMaximumLife = restoredResourceActor.Monster.MaxLife;
                uint restoredMaximumShield = restoredResourceActor.MaximumShield;
                Assert.Equal(Math.Min(savedLife, restoredMaximumLife), restoredResourceActor.Monster.Life);
                Assert.Equal(Math.Min(savedShield, restoredMaximumShield), restoredResourceActor.Shield);
                Assert.Equal(.375, restoredResourceActor.Regeneration);
                Assert.Equal(0d, restoredResourceActor.ShieldPool.Capture(1000).InterruptedSecondsAgo!.Value);
                AssertCreateResources(restoredResourcePackets, restoredResourceActor.Id,
                    restoredResourceActor.Monster.Life, restoredResourceActor.Shield,
                    restoredMaximumLife, restoredMaximumShield);

                uint noHealLife = restoredResourceActor.Monster.Life;
                uint noHealShield = restoredResourceActor.Shield;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, restoredResource, 1000);
                Assert.Equal(noHealLife, restoredResourceActor.Monster.Life);
                Assert.Equal(noHealShield, restoredResourceActor.Shield);
                Game.SaveGolems(restoredResource);
                var repeatedEntries = JsonSerializer.Deserialize<GolemState.Saved[]>(
                    CharacterSaves.ModuleData(resourceCharacter, "Game.Golems")!)!;
                var repeatedSaved = Assert.Single(repeatedEntries, entry => entry.Effect == resourceDefinition.Id);
                Assert.NotNull(repeatedSaved.Resources);
                Assert.Equal(noHealLife, repeatedSaved.Resources!.Life);
                Assert.Equal(noHealShield, repeatedSaved.Resources.EnergyShield.Current);
                Assert.Equal(.375, repeatedSaved.Resources.LifeRegeneration);

                resourceSchedule.At = 20.5;
                resource.Golems.RefreshAt = 0;
                var rebuildLife = freshResource.Monster.Life;
                var rebuildShield = freshResource.Shield;
                var rebuildBirth = freshResource.SummonedAt;
                Task<uint> preparedGolem;
                int levelTwenty = Program.Items.Gem(Program.Items.ByHash(resourceGem.TypeHash)!)!.ExperienceFor(20);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = [new(SocketColour.White, resourceGem with { GemExperience = levelTwenty })],
                });
                var raceAttacker = new SpawnedMonster("replacement race attacker", new(100, 100, 1), 100, 8, 1_000_000);
                var raceHit = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Physical, 1, 1)) { Spell = true };
                var golemRaceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(golemRaceGate.Wait(0));
                double rejectedIgniteUntil = default;
                try
                {
                    preparedGolem = Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                        resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: freshResource);
                    Assert.False(preparedGolem.IsCompleted, "the actual golem replacement did not reach the held create gate");
                    Assert.True(freshResource.Monster.Ignites.Add(20.5, 1, 2));
                    rejectedIgniteUntil = freshResource.Monster.Ignites.Until;
                    Assert.Equal(0, freshResource.Monster.Ignites.Advance(20.75, 1));
                    resourceSchedule.At = 20.75;
                    uint golemLifeBeforeHit = freshResource.Monster.Life;
                    uint golemShieldBeforeHit = freshResource.Shield;
                    resource.IncomingHitRandom = new LowestRandom();
                    Assert.True(Game.GolemTakeHitLocked(resource, freshResource, 0x09000031, raceAttacker, raceHit, 20.75));
                    Assert.True(freshResource.Monster.Life < golemLifeBeforeHit || freshResource.Shield < golemShieldBeforeHit);
                    rebuildLife = freshResource.Monster.Life;
                    rebuildShield = freshResource.Shield;
                }
                finally { golemRaceGate.Release(); }
                Assert.Equal(0u, await preparedGolem);
                Assert.Same(freshResource, resource.Golems.All[freshResource.Id]);
                Assert.Equal(rebuildLife, freshResource.Monster.Life);
                Assert.Equal(rebuildShield, freshResource.Shield);
                Assert.Equal(rejectedIgniteUntil, freshResource.Monster.Ignites.Until);
                Assert.Equal(1, freshResource.Monster.Ignites.Advance(21.75, 1));
                freshResource.Monster.Ignites.Clear();
                freshResource.Monster.Ignites.Buff = null;
                var rebuildSnapshot = freshResource.ShieldPool.Capture(resourceSchedule.At);
                var rebuildPackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At));
                var rebuiltResource = Assert.Single(resource.Golems.All).Value;
                Assert.True(rebuiltResource.Monster.MaxLife >= savedMaximumLife);
                Assert.Equal(rebuildBirth, rebuiltResource.SummonedAt);
                Assert.Equal(Math.Min(rebuildLife, rebuiltResource.Monster.MaxLife), rebuiltResource.Monster.Life);
                Assert.Equal(Math.Min(rebuildShield, rebuiltResource.MaximumShield), rebuiltResource.Shield);
                Assert.Equal(.375, rebuiltResource.Regeneration);
                Assert.Equal(rebuildSnapshot.InterruptedSecondsAgo,
                    rebuiltResource.ShieldPool.Capture(resourceSchedule.At).InterruptedSecondsAgo);
                AssertCreateResources(rebuildPackets, rebuiltResource.Id, rebuiltResource.Monster.Life,
                    rebuiltResource.Shield, rebuiltResource.Monster.MaxLife, rebuiltResource.MaximumShield);

                resourceSchedule.At = 20.75;
                rebuiltResource.Monster.Life = 1;
                rebuiltResource.ShieldPool.Empty();
                Dictionary<uint, int> sourceStatsBefore;
                using (new Game.PlayerStatScope(resource, fresh: true))
                    sourceStatsBefore = new(Game.CharacterPlayerStats(resource));
                Task<uint> preparedWithSource = null!;
                Task sourcePickup = null!;
                var sourceRaceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(sourceRaceGate.Wait(0));
                try
                {
                    preparedWithSource = Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                        resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: rebuiltResource);
                    Assert.False(preparedWithSource.IsCompleted);
                    Assert.True(rebuiltResource.Monster.Ignites.Add(20.75, 1, 2));
                    Assert.Equal(0, rebuiltResource.Monster.Ignites.Advance(21, 1));
                    sourcePickup = Game.PickUpItem("test", wire.Stream, wire.Ciphers, resource,
                        new InstanceItems.PickUpItem(PlayerInventories.InventoryId(PlayerInventories.BodyArmour),
                            armourSlot.Id, 3, 0, 0));
                    Assert.False(sourcePickup.IsCompleted, "public equipment pickup did not suspend behind the held create gate");
                    Assert.Empty(resourceInventory.Items(PlayerInventories.BodyArmour));
                    Assert.Equal(armourSlot.Id, Assert.Single(resourceInventory.Items(PlayerInventories.Cursor)).Id);
                    Dictionary<uint, int> sourceStatsAfter;
                    using (new Game.PlayerStatScope(resource, fresh: true))
                        sourceStatsAfter = new(Game.CharacterPlayerStats(resource));
                    Assert.Contains(sourceStatsBefore, pair => !sourceStatsAfter.TryGetValue(pair.Key, out int value)
                        || value != pair.Value);
                }
                finally { sourceRaceGate.Release(); }
                Assert.Equal(0u, await preparedWithSource);
                await sourcePickup;
                Assert.Same(rebuiltResource, resource.Golems.All[rebuiltResource.Id]);
                Assert.Equal(1u, rebuiltResource.Monster.Life);
                Assert.Equal(0u, rebuiltResource.Shield);
                Assert.Equal(1, rebuiltResource.Monster.Ignites.Advance(21.75, 1));
                rebuiltResource.Monster.Ignites.Clear();
                rebuiltResource.Monster.Ignites.Buff = null;

                await Game.PlaceItem("test", wire.Stream, wire.Ciphers, resource,
                    new InstanceItems.PlaceItem(PlayerInventories.InventoryId(PlayerInventories.BodyArmour),
                        0, 0, 0, 3, 0, 0));
                Assert.Equal(armourSlot.Id, Assert.Single(resourceInventory.Items(PlayerInventories.BodyArmour)).Id);
                resourceSchedule.At = 20.8;
                uint sourceRetryId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: rebuiltResource);
                Assert.NotEqual(0u, sourceRetryId);
                GolemState.Golem sourceRetry = resource.Golems.All[sourceRetryId];
                Assert.Same(sourceRetry, resource.Golems.All.Values.Single());
                Assert.Equal(1u, sourceRetry.Monster.Life);
                Assert.Equal(0u, sourceRetry.Shield);
                AssertIntegerPool(sourceRetry);

                // Restore the real socketed source to its intrinsic level-1 gem before proving that overriding raw
                // global gem stats, rather than the stored experience, drives the effective level and quality.
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with { Sockets = [new(SocketColour.White, resourceGem)] });
                RecalculateResource();
                resource.Golems.RefreshAt = 0;
                resourceSchedule.At = 20.9;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                sourceRetry = Assert.Single(resource.Golems.All).Value;
                Assert.Equal(1, sourceRetry.GemLevel);

                // Labelled nativePlayerStats diagnostic: the intrinsic socketed gem is level 1 / quality 0.
                // Raw global gem stats make the same source level 2 with positive quality, and the actual replacement
                // must admit the current actor while preserving its exact points.
                using (new Game.PlayerStatScope(resource, fresh: true))
                {
                    var intrinsic = Game.SupportedSkill(resource, resourceSource.Id);
                    Assert.Equal(1, intrinsic.Level);
                    Assert.Equal(0, intrinsic.Quality);
                }
                uint allGemLevel = Program.Stats.Key("all_skill_gem_level_+")!.Value;
                uint allGemQuality = Program.Stats.Key("all_skill_gem_quality_+")!.Value;
                resource.PlayerStats[allGemLevel] = 1;
                resource.PlayerStats[allGemQuality] = 10;
                var effective = Game.SupportedSkill(resource, resourceSource.Id);
                Assert.Equal(2, effective.Level);
                Assert.Equal(10, effective.Quality);
                sourceRetry.Monster.Life = 1;
                sourceRetry.ShieldPool.Empty();
                resourceSchedule.At = 21;
                var resourcePointLife = sourceRetry.Monster.Life;
                var resourcePointShield = sourceRetry.Shield;
                var stateCommitGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(stateCommitGate.Wait(0));
                Task<uint> preparedStateCommit = null!;
                EnergyShield.Snapshot stateCommitSnapshot = default;
                try
                {
                    preparedStateCommit = Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                        resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: sourceRetry);
                    Assert.False(preparedStateCommit.IsCompleted,
                        "the actual current-state replacement did not reach the held create gate");
                    // This is a neutral state control, not a source row: only the current live model is changed.
                    Assert.True(sourceRetry.Monster.Ignites.Add(21, 1, 2));
                    Assert.Equal(0, sourceRetry.Monster.Ignites.Advance(21.5, 1));
                    stateCommitSnapshot = sourceRetry.ShieldPool.Capture(21.5);
                    resourceSchedule.At = 21.5;
                }
                finally { stateCommitGate.Release(); }
                uint stateCommitId = await preparedStateCommit;
                Assert.NotEqual(0u, stateCommitId);
                sourceRetry = resource.Golems.All[stateCommitId];
                Assert.Single(resource.Golems.All);
                Assert.Equal(2, sourceRetry.GemLevel);
                Assert.Equal(10, sourceRetry.Quality);
                Assert.Equal(resourcePointLife, sourceRetry.Monster.Life);
                Assert.Equal(resourcePointShield, sourceRetry.Shield);
                Assert.Equal(stateCommitSnapshot.InterruptedSecondsAgo,
                    sourceRetry.ShieldPool.Capture(21.5).InterruptedSecondsAgo);
                Assert.True(sourceRetry.Monster.Ignites.Burning);
                Assert.Equal(1, sourceRetry.Monster.Ignites.Advance(22.5, 1));
                sourceRetry.Monster.Ignites.Clear();
                sourceRetry.Monster.Ignites.Buff = null;
                resource.PlayerStats.Remove(allGemLevel);
                resource.PlayerStats.Remove(allGemQuality);
                RecalculateResource();

                // A newer golem keeps the owner-wide flag true, but the old actor's own eight-second window is rechecked
                // when the held create commits. The newer actor is a real second SummonGolem, not a clock assignment.
                uint golemLimitKey = Program.Stats.Key("number_of_golems_allowed")!.Value;
                int previousRawGolemLimit = resource.PlayerStats.GetValueOrDefault(golemLimitKey);
                int skillGolemLimit = Game.SupportedSkill(resource, resourceSource.Id).Stats
                    .GetValueOrDefault(Program.Stats.Key("base_number_of_golems_allowed")!.Value);
                int LimitForRaw(int raw)
                {
                    var stats = new Dictionary<uint, int>(resource.PlayerStats) { [golemLimitKey] = raw };
                    return GolemCatalog.Limit(skillGolemLimit,
                        name => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0);
                }
                int capTwoRaw = Enumerable.Range(0, 16).First(raw => LimitForRaw(raw) == 2);
                resource.PlayerStats[golemLimitKey] = capTwoRaw;
                RecalculateResource();
                resourceSchedule.At = 28.1;
                uint youngerId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100));
                Assert.NotEqual(0u, youngerId);
                var younger = resource.Golems.All[youngerId];
                Assert.NotSame(sourceRetry, younger);
                Assert.Equal(20.25, sourceRetry.SummonedAt);
                Assert.Equal(28.1, younger.SummonedAt);
                Assert.Equal(2, resource.Golems.All.Count);
                Assert.Equal(2, younger.Limit);
                Assert.True(resource.Minions.Owns(sourceRetry.Id, sourceRetry.Monster));
                Assert.True(resource.Minions.Owns(younger.Id, younger.Monster));
                resourceSchedule.At = 28.2;
                Assert.True(Game.GolemSummonedInPastEightSeconds(sourceRetry, resourceSchedule.At));
                Assert.True(Game.GolemSummonedInPastEightSeconds(younger, resourceSchedule.At));
                Assert.True(Game.GolemSummonedInPastEightSeconds(resource, resourceSchedule.At));
                var youngWindowGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(youngWindowGate.Wait(0));
                Task<uint> staleYoungWindowReplacement = null!;
                try
                {
                    staleYoungWindowReplacement = Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                        resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: sourceRetry);
                    Assert.False(staleYoungWindowReplacement.IsCompleted);
                }
                finally
                {
                    resourceSchedule.At = 28.25;
                    youngWindowGate.Release();
                }
                Assert.Equal(0u, await staleYoungWindowReplacement);
                Assert.Same(sourceRetry, resource.Golems.All[sourceRetry.Id]);
                Assert.Same(younger, resource.Golems.All[younger.Id]);
                Assert.True(resource.Minions.Owns(sourceRetry.Id, sourceRetry.Monster));
                Assert.True(resource.Minions.Owns(younger.Id, younger.Monster));
                Assert.True(Game.GolemSummonedInPastEightSeconds(resource, 28.25));
                Assert.False(Game.GolemSummonedInPastEightSeconds(sourceRetry, 28.25));

                // The actual live entrypoint admits the same replacement after expiry, preserving the original birth.
                uint expiredSourceRetryId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: sourceRetry);
                Assert.NotEqual(0u, expiredSourceRetryId);
                sourceRetry = resource.Golems.All[expiredSourceRetryId];
                Assert.NotSame(sourceRetry, younger);
                Assert.Equal(20.25, sourceRetry.SummonedAt);
                Assert.Equal(0, Game.GolemRecentDamageIncrease(sourceRetry, 28.25));

                // Fixture cleanup only: remove the extra real younger actor and restore the original raw override for
                // the independent lethal current-state control below. This does not claim an actual cap of one.
                resource.Monsters.Remove(younger.Id);
                resource.Minions.Remove(younger.Id);
                resource.MinionOrigins.TryRemove(younger.Id, out _);
                resource.Golems.All.Remove(younger.Id);
                resource.PlayerStats[golemLimitKey] = previousRawGolemLimit;
                RecalculateResource();
                Assert.Single(resource.Golems.All);

                resourceSchedule.At = 20.9;
                Task<uint> preparedLethal = null!;
                var lethalRaceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(lethalRaceGate.Wait(0));
                try
                {
                    preparedLethal = Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                        resourceSource.Id, resourceDefinition, (100, 100), arrival: true, replacingOld: sourceRetry);
                    Assert.False(preparedLethal.IsCompleted);
                    Assert.True(Game.GolemTakeHitLocked(resource, sourceRetry, 0x09000032, raceAttacker,
                        raceHit with { Damage = DamagePacket.Of(DamageType.Physical, 1_000_000, 1_000_000) }, 20.9));
                    Assert.True(sourceRetry.Monster.Dead);
                }
                finally { lethalRaceGate.Release(); }
                Assert.Equal(0u, await preparedLethal);
                Assert.Same(sourceRetry, resource.Golems.All[sourceRetry.Id]);
                Assert.True(sourceRetry.Monster.Dead);

                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with { Sockets = [new(SocketColour.White, resourceGem)] });
                foreach (var removedGolem in resource.Golems.All.Values.ToArray())
                {
                    resource.Monsters.Remove(removedGolem.Id);
                    resource.Minions.Remove(removedGolem.Id);
                    resource.MinionOrigins.TryRemove(removedGolem.Id, out _);
                }
                resource.Golems.All.Clear();
                resource.Golems.RefreshAt = 0;

                string legacyGolemJson = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        currentSaved.Effect, currentSaved.Skill, Life = .42, ReviveSeconds = 0d,
                        currentSaved.SummonedSecondsRemaining,
                    },
                });
                Assert.DoesNotContain("\"Resources\"", legacyGolemJson);
                CharacterSaves.ChangeModuleData(resourceCharacter, "Game.Golems", _ => legacyGolemJson);
                var legacyResource = NewResourceClient("integer-resource-legacy", 1100);
                var legacyPackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, legacyResource, 1100));
                var legacyResourceActor = Assert.Single(legacyResource.Golems.All).Value;
                Assert.Equal((uint)(legacyResourceActor.Monster.MaxLife * .42), legacyResourceActor.Monster.Life);
                Assert.True(legacyResourceActor.MaximumShield > 0);
                Assert.Equal(0u, legacyResourceActor.Shield);
                Assert.Equal(0d, legacyResourceActor.ShieldPool.Capture(1100).InterruptedSecondsAgo!.Value);
                AssertCreateResources(legacyPackets, legacyResourceActor.Id, legacyResourceActor.Monster.Life, 0,
                    legacyResourceActor.Monster.MaxLife, legacyResourceActor.MaximumShield);
                double legacyDelay = EnergyShield.Delay(name => legacyResourceActor.Monster.Mods.Effects[name]);
                resourceSchedule.At = 1100 + legacyDelay - .01;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, legacyResource, resourceSchedule.At);
                Assert.Equal(0u, legacyResourceActor.Shield);
                resourceSchedule.At = 1100 + legacyDelay + .1;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, legacyResource, resourceSchedule.At);
                Assert.InRange(legacyResourceActor.Shield, 1u, legacyResourceActor.MaximumShield - 1);

                var zeroResources = new GolemState.MinionResourceSnapshot(0, 0, new(0, 0, 0));
                var zeroEntry = currentSaved with { Life = 0, ReviveSeconds = 0, Resources = zeroResources };
                CharacterSaves.ChangeModuleData(resourceCharacter, "Game.Golems",
                    _ => JsonSerializer.Serialize(new[] { zeroEntry }));
                var zeroResource = NewResourceClient("integer-resource-zero", 1200);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, zeroResource, 1200);
                var zeroActor = Assert.Single(zeroResource.Golems.All).Value;
                Assert.True(zeroActor.Monster.Dead);
                Assert.Equal(0u, zeroActor.Monster.Life);
                Assert.Equal(0u, zeroActor.Shield);
                Assert.Equal(1202, zeroActor.ReviveAt);

                var waitingEntry = currentSaved with
                {
                    Life = 0, ReviveSeconds = .5, Resources = zeroResources,
                };
                CharacterSaves.ChangeModuleData(resourceCharacter, "Game.Golems",
                    _ => JsonSerializer.Serialize(new[] { waitingEntry }));
                var waitingResource = NewResourceClient("integer-resource-waiting", 1300);
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, waitingResource, 1300);
                Assert.Empty(waitingResource.Golems.All);
                Assert.Single(waitingResource.Golems.Waiting);
                resourceSchedule.At = 1300.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, waitingResource, resourceSchedule.At);
                var revivedResource = Assert.Single(waitingResource.Golems.All).Value;
                Assert.False(revivedResource.Monster.Dead);
                Assert.Equal(revivedResource.Monster.MaxLife, revivedResource.Monster.Life);
                Assert.Equal(revivedResource.MaximumShield, revivedResource.Shield);

                var neverCrit = new SpawnedMonster("integer resource attacker", new(100, 100, 1), 100, 8, 1_000_000)
                {
                    Mods = MonsterModSet.None with
                    {
                        Effects = new(new Dictionary<string, int>
                        {
                            ["critical_strike_chance_+%"] = -10_000,
                            ["accuracy_rating_+%"] = -100,
                        }),
                    },
                };
                Assert.Equal(0, neverCrit.Effects.CriticalChance);
                Assert.Equal(5, DamageModel.AccuracyHitChance(0, 0));
                MonsterAttack FixedHit(DamageType type, int amount) =>
                    new(0x4000, 1, 100, DamagePacket.Of(type, amount, amount)) { Spell = true };

                resourceSchedule.At = 20;
                uint offId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                var offActor = resource.Golems.All[offId];
                Assert.Equal(0, offActor.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(20, Game.GolemPools.Calculate(Game.Monsters, offActor.Monster.Variety!,
                    offActor.Monster.Level, offActor, offActor.Player, offActor.Monster.Mods.Effects).ChaosResistance);
                AssertIntegerPool(offActor);
                offActor.Monster.Life = 600;
                uint offLife = offActor.Monster.Life, offShield = offActor.Shield;
                Assert.True(Game.GolemTakeHitLocked(resource, offActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Chaos, 400), 20.1));
                Assert.Equal(0, offActor.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(offShield, offActor.Shield);
                Assert.Equal(offLife - 320, offActor.Monster.Life);
                AssertConservation(offActor, offLife, offShield, offActor.Monster.Life, offActor.Shield, 320);

                resourceInventory.Change(PlayerInventories.BodyArmour, armourSlot.Id,
                    item => item with { Mods = originalArmourMods });
                RecalculateResource();
                uint onId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                Assert.NotEqual(offId, onId);
                var onActor = resource.Golems.All[onId];
                Assert.True(onActor.Monster.MaxLife > 600);
                onActor.Monster.Life = 600;
                uint onLife = onActor.Monster.Life, onShield = onActor.Shield;
                Assert.True(onShield > 320);
                Assert.Equal(onShield, onActor.MaximumShield);
                Assert.True(Game.GolemTakeHitLocked(resource, onActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Chaos, 400), 20.2));
                Assert.Equal(1, onActor.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                AssertIntegerPool(onActor);
                Assert.Equal(onLife, onActor.Monster.Life);
                Assert.Equal(onShield - 320, onActor.Shield);
                AssertConservation(onActor, onLife, onShield, onActor.Monster.Life, onActor.Shield, 320);

                uint physicalLife = onActor.Monster.Life, physicalShield = onActor.Shield;
                Assert.True(Game.GolemTakeHitLocked(resource, onActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Physical, 50), 20.3));
                Assert.Equal(physicalShield - 50, onActor.Shield);
                Assert.Equal(physicalLife, onActor.Monster.Life);
                AssertConservation(onActor, physicalLife, physicalShield, onActor.Monster.Life, onActor.Shield, 50);

                uint spillLife = onActor.Monster.Life, spillShield = onActor.Shield;
                Assert.True(spillShield < 320);
                Assert.True(Game.GolemTakeHitLocked(resource, onActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Chaos, 400), 20.4));
                Assert.Equal(spillLife - (320 - spillShield), onActor.Monster.Life);
                Assert.Equal(0u, onActor.Shield);
                AssertConservation(onActor, spillLife, spillShield, onActor.Monster.Life, onActor.Shield, 320);

                resourceSchedule.At = 30;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                uint retirementId = onActor.Id;
                uint retirementLife = onActor.Monster.Life;
                uint completeMaximumShield = onActor.MaximumShield;
                Assert.Same(onActor, resource.Golems.All[retirementId]);
                Assert.Equal(517u, retirementLife);
                Assert.Equal(completeMaximumShield, onActor.Shield);

                uint removedPlacementId = armourSlot.Id;
                Assert.NotNull(resourceInventory.Remove(PlayerInventories.BodyArmour, removedPlacementId));
                RecalculateResource();
                Assert.True(Game.GolemTakeHitLocked(resource, onActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Chaos, 400), 30.1));
                Assert.Same(onActor, resource.Golems.All[retirementId]);
                Assert.False(onActor.Monster.Dead);
                Assert.Equal(0, onActor.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                uint bareMaximumShield = (uint)Math.Floor(onActor.Monster.MaxLife * .15d);
                Assert.Equal(bareMaximumShield, onActor.MaximumShield);
                Assert.Equal(Math.Min(completeMaximumShield, bareMaximumShield), onActor.Shield);
                Assert.Equal(retirementLife - 320, onActor.Monster.Life);
                Assert.Equal(197u, onActor.Monster.Life);

                armourSlot = resourceInventory.Add(PlayerInventories.BodyArmour, 0, 0,
                    resourceArmour with { Mods = originalArmourMods });
                RecalculateResource();
                uint relocatedPlacementId = armourSlot.Id;
                uint bareShield = onActor.Shield, bareLife = onActor.Monster.Life;
                Assert.Equal(bareMaximumShield, bareShield);
                Assert.True(Game.GolemTakeHitLocked(resource, onActor, 0x08000010, neverCrit,
                    FixedHit(DamageType.Chaos, 100), 30.2));
                Assert.Same(onActor, resource.Golems.All[retirementId]);
                Assert.Equal(1, onActor.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(completeMaximumShield, onActor.MaximumShield);
                Assert.Equal(bareShield - 80, onActor.Shield);
                Assert.Equal(85u, onActor.Shield);
                Assert.Equal(bareLife, onActor.Monster.Life);
                Assert.Equal(197u, onActor.Monster.Life);
                Assert.Equal(relocatedPlacementId, armourSlot.Id);

                SetDelay(50);
                resourceSchedule.At = 40;
                uint fastId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                var fast = resource.Golems.All[fastId];
                Assert.Equal(50, fast.Monster.Mods.Effects["energy_shield_delay_-%"]);
                uint fastFull = fast.Shield;
                Assert.True(Game.GolemTakeHitLocked(resource, fast, 0x08000010, neverCrit,
                    FixedHit(DamageType.Physical, 100), 40.5));
                uint fastDamaged = fast.Shield;
                Assert.True(fastDamaged < fastFull);
                double fastDelay = EnergyShield.Delay(name => fast.Monster.Mods.Effects[name]);
                Assert.Equal(4 / 3d, fastDelay, 12);
                resourceSchedule.At = 40.5 + fastDelay;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(fastDamaged, fast.Shield);
                resourceSchedule.At = 40.7;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(fastDamaged, fast.Shield);
                resourceSchedule.At = 40.5 + fastDelay + .2;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(fastDamaged + (uint)Math.Floor(fast.MaximumShield / 3d * .2), fast.Shield);

                SetDelay(100);
                resourceSchedule.At = 50;
                uint slowId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                var slow = resource.Golems.All[slowId];
                Assert.Equal(100, slow.Monster.Mods.Effects["energy_shield_delay_-%"]);
                Assert.True(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit,
                    FixedHit(DamageType.Physical, 100), 50.5));
                uint slowDamaged = slow.Shield;
                Assert.True(slowDamaged < slow.MaximumShield);
                Assert.True(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit,
                    FixedHit(DamageType.Physical, 0), 50.6));
                resource.PlayerStats[Program.Stats.Key("elemental_golem_immunity_to_elemental_damage")!.Value] = 1;
                Assert.True(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit,
                    FixedHit(DamageType.Fire, 100), 50.7));
                resource.PlayerStats[Program.Stats.Key("elemental_golem_immunity_to_elemental_damage")!.Value] = 0;
                Assert.Equal(slowDamaged, slow.Shield);
                resourceSchedule.At = 51.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(slowDamaged, slow.Shield);
                resourceSchedule.At = 51.7;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(slowDamaged + (uint)Math.Floor(slow.MaximumShield / 3d * .2), slow.Shield);

                resource.IncomingHitRandom = new HighestRandom();
                var missed = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Physical, 100, 100));
                uint beforeMiss = slow.Shield;
                Assert.False(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit, missed, 51.8));
                Assert.Equal(beforeMiss, slow.Shield);
                resourceSchedule.At = 52;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(slow.MaximumShield, slow.Shield);

                Assert.True(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit,
                    FixedHit(DamageType.Physical, 100), 53));
                uint partialShield = slow.Shield;
                Assert.Equal(slow.MaximumShield - 100, partialShield);
                resource.PlayerStats[Program.Stats.Key("minion_additional_spell_block_%")!.Value] = 100;
                resource.IncomingHitRandom = new LowestRandom();
                Assert.False(Game.GolemTakeHitLocked(resource, slow, 0x08000010, neverCrit,
                    FixedHit(DamageType.Fire, 100), 53.5));
                Assert.Equal(partialShield, slow.Shield);
                resource.PlayerStats[Program.Stats.Key("minion_additional_spell_block_%")!.Value] = 0;
                resourceSchedule.At = 54;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(partialShield, slow.Shield);
                resourceSchedule.At = 54.2;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(partialShield + (uint)Math.Floor(slow.MaximumShield / 3d * .2), slow.Shield);

                SetDelay(100);
                resourceSchedule.At = 60;
                uint dotId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                var dotGolem = resource.Golems.All[dotId];
                var fireSource = new SpawnedMonster("deterministic igniter", new(100, 100, 1), 100, 8, 1_000_000)
                {
                    Mods = MonsterModSet.None with
                    {
                        Effects = new(new Dictionary<string, int>
                        {
                            ["critical_strike_chance_+%"] = -10_000,
                            ["base_chance_to_ignite_%"] = 100,
                        }),
                    },
                };
                var fireAttack = new MonsterAttack(0x4000, 1, 100, DamagePacket.Of(DamageType.Fire, 20, 20))
                {
                    Spell = true,
                };
                fireSource.Attack = fireAttack;
                resource.IncomingHitRandom = new LowestRandom();
                uint dotLife = dotGolem.Monster.Life;
                Assert.True(Game.GolemTakeHitLocked(resource, dotGolem, 0x08000020, fireSource, fireAttack, 60.1));
                dotLife = dotGolem.Monster.Life;
                Assert.True(dotGolem.Monster.Ignites.Burning);
                Assert.Equal(18, dotGolem.Monster.Ignites.PerSecond(60.1), 12);

                uint fireShield = dotGolem.Shield;
                double shortFireFactor = Game.MonsterDotTaken(dotGolem.Monster, DamageType.Fire, 60.6);
                int shortFireDamage = (int)Math.Floor(18 * shortFireFactor * .5);
                int dotOutputStart = resource.Minions.OutputQueue.Count;
                resourceSchedule.At = 60.6;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(fireShield - (uint)shortFireDamage, dotGolem.Shield);
                Assert.Equal(dotLife, dotGolem.Monster.Life);
                var shortTickOutputs = resource.Minions.OutputQueue.Skip(dotOutputStart).ToArray();
                Assert.DoesNotContain(shortTickOutputs, output => output.Kind == PlayerMinions.OutputKind.Flinch);
                Assert.DoesNotContain(shortTickOutputs, output => output.Log is "golem takes damage" or "golem died");

                resourceSchedule.At = 64.05;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.False(dotGolem.ShieldPool.Recharging);
                uint throughBurn = dotGolem.Shield;
                resourceSchedule.At = 65.1;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(throughBurn, dotGolem.Shield);
                resourceSchedule.At = 65.2;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(throughBurn + (uint)Math.Floor(dotGolem.MaximumShield / 3d * .1), dotGolem.Shield);

                // A real positive fire hit depopulates ES; the following no-hit burn tick then spills exactly once to Life.
                var fireResistance = Game.MonsterHitResistance(dotGolem.Monster, DamageType.Fire, default);
                int cappedFireMaximum = Math.Clamp(fireResistance.Maximum
                    + dotGolem.Monster.Mods.Effects["base_maximum_fire_damage_resistance_%"], 0, 90);
                int IncomingFire(int amount) => (int)Math.Round((int)Math.Round(DamageModel.Mitigate(
                    DamagePacket.Of(DamageType.Fire, amount, amount),
                    _ => fireResistance with { Maximum = cappedFireMaximum })[DamageType.Fire].Min)
                    * dotGolem.Monster.Mods.Effects.DamageTaken(spell: true));
                int low = 1, high = int.MaxValue;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if ((uint)IncomingFire(middle) > dotGolem.Shield) high = middle;
                    else low = middle + 1;
                }
                fireAttack = fireAttack with
                {
                    Damage = DamagePacket.Of(DamageType.Fire, low, low),
                };
                fireSource.Attack = fireAttack;
                resourceSchedule.At = 66;
                Assert.True(Game.GolemTakeHitLocked(resource, dotGolem, 0x08000020, fireSource, fireAttack, 66));
                Assert.Equal(0u, dotGolem.Shield);
                dotLife = dotGolem.Monster.Life;
                Assert.True(dotLife > 0);
                double spillFactor = Game.MonsterDotTaken(dotGolem.Monster, DamageType.Fire, 66.1);
                int expectedSpill = (int)Math.Floor(dotGolem.Monster.Ignites.PerSecond(66.1) * spillFactor * .1);
                resourceSchedule.At = 66.1;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(dotLife - (uint)expectedSpill, dotGolem.Monster.Life);
                Assert.Equal(0u, dotGolem.Shield);

                // A later real hit keeps its later recharge interruption when a coarse tick processes an earlier burn expiry.
                dotGolem.Monster.Ignites.Clear();
                dotGolem.Monster.Ignites.Buff = null;
                resourceSchedule.At = 68.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                dotGolem.ShieldPool.SetMaximum(dotGolem.MaximumShield, refill: true);
                var crossingHit = fireAttack with { Damage = DamagePacket.Of(DamageType.Physical, 100, 100) };
                Assert.True(Game.GolemTakeHitLocked(resource, dotGolem, 0x08000020, fireSource, crossingHit, 68.5));
                Assert.True(dotGolem.Monster.Ignites.Add(68.5, 10, .5));
                Assert.True(Game.GolemTakeHitLocked(resource, dotGolem, 0x08000020, fireSource, crossingHit, 69.25));
                resourceSchedule.At = 69.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.False(dotGolem.Monster.Ignites.Burning);
                Assert.False(dotGolem.ShieldPool.Recharging);
                uint crossingDamage = dotGolem.Shield;
                resourceSchedule.At = 70.25;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(crossingDamage, dotGolem.Shield);
                resourceSchedule.At = 70.5;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(crossingDamage + (uint)Math.Floor(dotGolem.MaximumShield / 3d * .25), dotGolem.Shield);

                // Real catalogue rows supply hostile bleed/poison; the owned monster owns their strongest/stacking sets.
                var rhoa = Game.Monsters.Find("Metadata/Monsters/Rhoas/Rhoa")!;
                var poisonMod = Program.Items.Mods.ById("MonsterModApplyPoison")!;
                var bleedMod = Program.Items.Mods.ById("MonsterModApplyBleed")!;
                Assert.Equal(MonsterModCatalog.Domain, poisonMod.Domain);
                Assert.Equal(MonsterModCatalog.Domain, bleedMod.Domain);
                Assert.Contains(poisonMod.GenerationType, new[] { MonsterModCatalog.Prefix, MonsterModCatalog.Suffix });
                Assert.Contains(bleedMod.GenerationType, new[] { MonsterModCatalog.Prefix, MonsterModCatalog.Suffix });
                var hostileMods = Game.MonsterModRolls.Value.Roll(rhoa, 80, MonsterRarity.Magic,
                    new Random(504), alone: true, count: 2, forced: [poisonMod, bleedMod]);
                Assert.Equal(1, hostileMods.Effects["global_poison_on_hit"]);
                Assert.Equal(1, hostileMods.Effects["global_bleed_on_hit"]);

                resourceSchedule.At = 80;
                uint hostileId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource, resourceSource.Id,
                    resourceDefinition, (100, 100));
                var hostileTarget = resource.Golems.All[hostileId];
                var victimMods = hostileTarget.Monster.Mods;
                var hostileSource = new SpawnedMonster("Rhoa ailment source", new(100, 100, 1), 100, 8, 1_000_000)
                {
                    Mods = hostileMods,
                };
                var hostileAttack = new MonsterAttack(0x4000, 1, 100,
                    DamagePacket.Of(DamageType.Physical, 200, 200)) { Spell = true };
                hostileSource.Attack = hostileAttack;
                var incomingBefore = resource.IncomingAilments.Snapshot(80);
                uint playerLifeBefore = resource.PlayerLife;
                resource.IncomingHitRandom = new LowestRandom();
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    hostileAttack, 80));
                Assert.True(hostileTarget.Monster.Bleeds.Burning);
                Assert.True(hostileTarget.Monster.Poisons.Poisoned);
                Assert.Equal(incomingBefore, resource.IncomingAilments.Snapshot(80));
                Assert.Equal(playerLifeBefore, resource.PlayerLife);
                Assert.Equal(20, Game.GolemPools.Calculate(Game.Monsters, hostileTarget.Monster.Variety!,
                    hostileTarget.Monster.Level, hostileTarget, hostileTarget.Player, hostileTarget.Monster.Mods.Effects)
                    .ChaosResistance);
                Assert.Equal(.8, Game.MonsterDotTaken(hostileTarget.Monster, DamageType.Chaos, 80), 12);

                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Bleeds.Buff = null;
                double poisonDps = hostileTarget.Monster.Poisons.TotalDps(80);
                Assert.Equal(60, poisonDps, 12);
                uint poisonedShield = hostileTarget.Shield;
                uint poisonedLife = hostileTarget.Monster.Life;
                resourceSchedule.At = 80.2;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(1, hostileTarget.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(poisonedLife, hostileTarget.Monster.Life);
                Assert.Equal(poisonedShield - 9, hostileTarget.Shield);

                resourceInventory.Change(PlayerInventories.BodyArmour, armourSlot.Id,
                    item => item with { Mods = item.Mods.Where(mod => mod.Hash != noBypass.Hash).ToArray() });
                RecalculateResource();
                poisonedShield = hostileTarget.Shield;
                poisonedLife = hostileTarget.Monster.Life;
                resourceSchedule.At = 80.4;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(0, hostileTarget.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(poisonedShield, hostileTarget.Shield);
                Assert.Equal(poisonedLife - 10, hostileTarget.Monster.Life);

                resourceInventory.Change(PlayerInventories.BodyArmour, armourSlot.Id,
                    item => item with { Mods = originalArmourMods });
                RecalculateResource();
                poisonedShield = hostileTarget.Shield;
                poisonedLife = hostileTarget.Monster.Life;
                resourceSchedule.At = 80.6;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(1, hostileTarget.Monster.Mods.Effects["chaos_damage_does_not_bypass_energy_shield"]);
                Assert.Equal(poisonedLife, hostileTarget.Monster.Life);
                Assert.Equal(poisonedShield - 9, hostileTarget.Shield);

                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Poisons.Buff = null;
                Assert.True(hostileTarget.Monster.Poisons.Add(81, 200, 1));
                Assert.True(hostileTarget.Monster.Poisons.Add(81, 301, 1));
                Assert.Equal(501, hostileTarget.Monster.Poisons.TotalDps(81), 12);
                poisonedShield = hostileTarget.Shield;
                poisonedLife = hostileTarget.Monster.Life;
                resourceSchedule.At = 81.25;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.Equal(poisonedLife, hostileTarget.Monster.Life);
                Assert.Equal(poisonedShield - 100, hostileTarget.Shield);

                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Poisons.Buff = null;
                uint armourKey = Program.Stats.Key("minion_physical_damage_reduction_rating")!.Value;
                resource.PlayerStats[armourKey] = 1_000_000;
                RecalculateResource();
                hostileTarget.Monster.Walk.Order(hostileTarget.Monster.Walk.Position(81.5).X,
                    hostileTarget.Monster.Walk.Position(81.5).Y, 81.5);
                Assert.True(hostileTarget.Monster.Bleeds.Add(81.5, 100, 1));
                Assert.True(hostileTarget.Monster.Bleeds.Add(81.5, 201, 1));
                Assert.False(hostileTarget.Monster.Walk.Moving(81.75));
                Assert.Equal(1, Game.BleedTaken(hostileTarget.Monster, 81.75), 12);
                uint bleedShield = hostileTarget.Shield;
                uint bleedLife = hostileTarget.Monster.Life;
                resourceSchedule.At = 81.75;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.True(hostileTarget.Monster.Mods.Effects["physical_damage_reduction_rating"] >= 1_000_000);
                Assert.Equal(bleedLife, hostileTarget.Monster.Life);
                Assert.Equal(bleedShield - 50, hostileTarget.Shield);
                resource.PlayerStats.Remove(armourKey);
                RecalculateResource();

                // Bleed uses the shared self-duration contract, not ignite's reduction; poison applies its base-self
                // reduction and the shared contract once. The custom rows are labelled diagnostics.
                Assert.Equal(1, IncomingDamagingAilments.SelfDuration(null, bleed: true));
                Assert.Equal(2, IncomingDamagingAilments.SelfDuration(
                    name => name == "combined_bleeding_duration_on_self_+%" ? 100 : 0, bleed: true));
                var durationSource = hostileMods with
                {
                    Effects = hostileMods.Effects.Plus(new MonsterModEffects(new Dictionary<string, int>
                    {
                        ["base_bleed_duration_+%"] = 25,
                        ["base_poison_duration_+%"] = 25,
                    })),
                };
                hostileSource.Mods = durationSource;
                hostileTarget.Monster.Ignites.Clear();
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Mods = victimMods with
                {
                    Effects = victimMods.Effects.Plus(new MonsterModEffects(new Dictionary<string, int>
                    {
                        ["base_self_ignite_duration_-%"] = 100,
                        ["base_self_poison_duration_-%"] = 50,
                    })),
                };
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    DamagePacket.Of(DamageType.Physical, 200, 200), critical: false, attack: false, 81.875);
                Assert.Equal(6.25, hostileTarget.Monster.Bleeds.Until - 81.875, 12);
                Assert.Equal(1.25, hostileTarget.Monster.Poisons.Until - 81.875, 12);

                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Mods = victimMods with
                {
                    Effects = victimMods.Effects.Plus(new MonsterModEffects(new Dictionary<string, int>
                    {
                        ["base_self_poison_duration_-%"] = 50,
                        ["combined_bleeding_duration_on_self_+%"] = 100,
                        ["self_bleed_duration_+%_final"] = 50,
                        ["combined_poison_duration_on_self_+%"] = 100,
                        ["self_poison_duration_+%_final"] = 50,
                    })),
                };
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    DamagePacket.Of(DamageType.Physical, 200, 200), critical: false, attack: false, 82);
                Assert.Equal(18.75, hostileTarget.Monster.Bleeds.Until - 82, 12);
                Assert.Equal(3.75, hostileTarget.Monster.Poisons.Until - 82, 12);
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileSource.Mods = hostileMods;

                // Prepared victim cannot/immune/avoid rows reject both real hostile ailment sources.
                hostileTarget.Monster.Ignites.Clear();
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Ignites.Buff = null;
                hostileTarget.Monster.Bleeds.Buff = null;
                hostileTarget.Monster.Poisons.Buff = null;
                hostileTarget.LastStep = 82.1;
                resourceSchedule.At = 82.1;
                Game.GolemStepLocked("test", wire.Stream, wire.Ciphers, resource, hostileTarget, 82.1);
                hostileTarget.Monster.Mods = victimMods with
                {
                    Effects = victimMods.Effects.Plus(new MonsterModEffects(new Dictionary<string, int>
                    {
                        ["cannot_be_poisoned"] = 1,
                        ["immune_to_bleeding"] = 1,
                    })),
                };
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                resourceSchedule.At = 82.125;
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    DamagePacket.Of(DamageType.Physical, 200, 200), critical: false, attack: true, 82.125);
                Assert.False(hostileTarget.Monster.Bleeds.Burning);
                Assert.False(hostileTarget.Monster.Poisons.Poisoned);
                Assert.Equal(1, hostileTarget.Monster.Mods.Effects["cannot_be_poisoned"]);
                Assert.Equal(1, hostileTarget.Monster.Mods.Effects["immune_to_bleeding"]);

                hostileTarget.Monster.Mods = victimMods with
                {
                    Effects = victimMods.Effects.Plus(new MonsterModEffects(new Dictionary<string, int>
                    {
                        ["avoid_poison_%"] = 100,
                        ["avoid_bleed_%"] = 100,
                    })),
                };
                resource.IncomingHitRandom = new LowestRandom();
                resourceSchedule.At = 82.25;
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    DamagePacket.Of(DamageType.Physical, 200, 200), critical: false, attack: true, 82.25);
                Assert.False(hostileTarget.Monster.Bleeds.Burning);
                Assert.False(hostileTarget.Monster.Poisons.Poisoned);
                hostileTarget.Monster.Mods = victimMods;

                // Attack-only source rows follow the current contact, not a different cached source ability.
                hostileSource.Mods = MonsterModSet.None with
                {
                    Effects = new(new Dictionary<string, int>
                    {
                        ["bleed_on_hit_with_attacks_%"] = 100,
                        ["base_attacks_poison"] = 1,
                    }),
                };
                hostileSource.Attack = hostileAttack with { Spell = false };
                hostileTarget.Monster.Ignites.Clear();
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                resource.IncomingHitRandom = new LowestRandom();
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    hostileAttack with { Spell = true }, 82.375));
                Assert.False(hostileTarget.Monster.Bleeds.Burning);
                Assert.False(hostileTarget.Monster.Poisons.Poisoned);

                hostileSource.Attack = hostileAttack with { Spell = true };
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    hostileAttack with { Spell = false }, 82.5));
                Assert.True(hostileTarget.Monster.Bleeds.Burning);
                Assert.True(hostileTarget.Monster.Poisons.Poisoned);
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Poisons.Clear();
                hostileSource.Mods = hostileMods;

                // Actual accepted hit routes admit both status paths. The deterministic igniter is the labelled
                // diagnostic source; the hostile physical contact uses the real catalogue poison/bleed rows.
                var transferFireAttack = fireAttack with
                {
                    Damage = DamagePacket.Of(DamageType.Fire, 20, 20), Spell = true,
                };
                var transferHostileAttack = hostileAttack with
                {
                    Damage = DamagePacket.Of(DamageType.Physical, 20, 20), Spell = true,
                };
                fireSource.Attack = transferFireAttack;
                hostileSource.Mods = hostileMods;
                hostileSource.Attack = transferHostileAttack;
                resource.IncomingHitRandom = new LowestRandom();
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, fireSource,
                    transferFireAttack, 82.75));
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    transferHostileAttack, 82.75));
                var alwaysCrit = Program.Items.Mods.ById("MonsterModAlwaysCrit")!;
                Assert.Equal(MonsterModCatalog.Domain, alwaysCrit.Domain);
                var elementalMods = Game.MonsterModRolls.Value.Roll(rhoa, 80, MonsterRarity.Magic,
                    new Random(504), alone: true, count: 1, forced: [alwaysCrit]);
                Assert.Equal(1, elementalMods.Effects["always_crit"]);
                var elementalSource = new SpawnedMonster("catalogue always-critical elemental source",
                    new(100, 100, 1), 100, 8, 1_000_000) { Mods = elementalMods };
                int elementalAmount = (int)Math.Clamp(
                    ((long)hostileTarget.Monster.Life + hostileTarget.Shield) / 10, 1, 100);
                var elementalAttack = new MonsterAttack(0x4000, 1, 100,
                    DamagePacket.Of(DamageType.Cold, elementalAmount, elementalAmount)
                        .Add(DamagePacket.Of(DamageType.Lightning, elementalAmount, elementalAmount)))
                { Spell = true };
                elementalSource.Attack = elementalAttack;
                Assert.True(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000022, elementalSource,
                    elementalAttack, 82.75));
                Assert.True(hostileTarget.Monster.Life > 0);
                Assert.True(hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Chill, 82.75) > 0);
                Assert.True(hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Freeze, 82.75) > 0);
                Assert.True(hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Shock, 82.75) > 0);
                double chillRemaining = hostileTarget.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Chill, 82.75);
                double freezeRemaining = hostileTarget.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Freeze, 82.75);
                double shockRemaining = hostileTarget.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Shock, 82.75);
                double chillMagnitude = hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Chill, 82.75);
                double freezeMagnitude = hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Freeze, 82.75);
                double shockMagnitude = hostileTarget.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Shock, 82.75);
                Assert.True(hostileTarget.Monster.Ignites.Burning);
                Assert.True(hostileTarget.Monster.Bleeds.Burning);
                Assert.True(hostileTarget.Monster.Poisons.Poisoned);
                uint movedLife = hostileTarget.Monster.Life;
                uint movedShield = hostileTarget.Shield;
                var movedPool = hostileTarget.ShieldPool.Capture(82.75);
                uint oldGolemId = hostileTarget.Id;
                double igniteUntil = hostileTarget.Monster.Ignites.Until;
                double bleedUntil = hostileTarget.Monster.Bleeds.Until;
                double poisonUntil = hostileTarget.Monster.Poisons.Until;
                double movedIgniteDps = hostileTarget.Monster.Ignites.PerSecond(82.75);
                double movedBleedDps = hostileTarget.Monster.Bleeds.PerSecond(82.75);
                double movedPoisonDps = hostileTarget.Monster.Poisons.TotalDps(82.75);
                int movedPoisonStacks = hostileTarget.Monster.Poisons.Stacks(82.75);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == resourceGem.TypeHash
                        ? socket with { Gem = resourceGem with { GemExperience = levelTwenty } } : socket).ToArray(),
                });
                RecalculateResource();
                resource.Golems.RefreshAt = 0;
                resourceSchedule.At = 82.75;
                int rosterBeforeRescale = resource.Golems.All.Count;
                var rescaledGolemPackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, 82.75));
                int rosterAfterRescale = resource.Golems.All.Count;
                Assert.Equal(rosterBeforeRescale, rosterAfterRescale);
                var rescaledGolem = Assert.Single(resource.Golems.All.Values, golem =>
                    golem.Id != oldGolemId
                    && golem.Monster.Ignites.Burning && golem.Monster.Ignites.BuffSource == 0x08000021
                    && golem.Monster.Ignites.Until == igniteUntil
                    && golem.Monster.Bleeds.Burning && golem.Monster.Bleeds.BuffSource == 0x08000021
                    && golem.Monster.Bleeds.Until == bleedUntil
                    && golem.Monster.Poisons.Poisoned && golem.Monster.Poisons.BuffSource == 0x08000021
                    && golem.Monster.Poisons.Until == poisonUntil
                    && golem.Monster.Ailments.Remaining(MonsterElementalAilments.Kind.Chill, 82.75) == chillRemaining
                    && golem.Monster.Ailments.Remaining(MonsterElementalAilments.Kind.Freeze, 82.75) == freezeRemaining
                    && golem.Monster.Ailments.Remaining(MonsterElementalAilments.Kind.Shock, 82.75) == shockRemaining);
                Assert.NotSame(hostileTarget, rescaledGolem);
                Assert.Same(rescaledGolem, resource.Golems.All[rescaledGolem.Id]);
                Assert.DoesNotContain(oldGolemId, resource.Golems.All.Keys);
                Assert.True(rescaledGolem.GemLevel > 1);
                Assert.Equal(movedLife, rescaledGolem.Monster.Life);
                Assert.Equal(movedShield, rescaledGolem.Shield);
                Assert.Equal(movedPool.InterruptedSecondsAgo,
                    rescaledGolem.ShieldPool.Capture(82.75).InterruptedSecondsAgo);
                Assert.Equal(chillRemaining, rescaledGolem.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Chill, 82.75));
                Assert.Equal(freezeRemaining, rescaledGolem.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Freeze, 82.75));
                Assert.Equal(shockRemaining, rescaledGolem.Monster.Ailments.Remaining(
                    MonsterElementalAilments.Kind.Shock, 82.75));
                Assert.Equal(chillMagnitude, rescaledGolem.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Chill, 82.75));
                Assert.Equal(freezeMagnitude, rescaledGolem.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Freeze, 82.75));
                Assert.Equal(shockMagnitude, rescaledGolem.Monster.Ailments.Magnitude(
                    MonsterElementalAilments.Kind.Shock, 82.75));
                Assert.Equal(0, rescaledGolem.Monster.Ailments.ActionFactor(82.75));
                Assert.Equal(igniteUntil, rescaledGolem.Monster.Ignites.Until);
                Assert.Equal(bleedUntil, rescaledGolem.Monster.Bleeds.Until);
                Assert.Equal(poisonUntil, rescaledGolem.Monster.Poisons.Until);
                Assert.Equal(movedIgniteDps, rescaledGolem.Monster.Ignites.PerSecond(82.75), 12);
                Assert.Equal(movedBleedDps, rescaledGolem.Monster.Bleeds.PerSecond(82.75), 12);
                Assert.Equal(movedPoisonDps, rescaledGolem.Monster.Poisons.TotalDps(82.75), 12);
                Assert.Equal(movedPoisonStacks, rescaledGolem.Monster.Poisons.Stacks(82.75));
                Assert.Equal(0x08000021u, rescaledGolem.Monster.Ignites.BuffSource);
                Assert.Equal(0x08000021u, rescaledGolem.Monster.Bleeds.BuffSource);
                Assert.Equal(0x08000021u, rescaledGolem.Monster.Poisons.BuffSource);
                byte[] currentIgniteBuff = InstanceEntities.AddBuff(rescaledGolem.Id,
                    rescaledGolem.Monster.Ignites.Buff!.Value, Game.IgnitedBuffRow, 0x08000021,
                    stacks: 1, u68: BitConverter.SingleToUInt32Bits((float)(igniteUntil - 82.75)));
                byte[] currentBleedBuff = InstanceEntities.AddBuff(rescaledGolem.Id,
                    rescaledGolem.Monster.Bleeds.Buff!.Value, LabyrinthTrials.BleedingBuffRow!.Value,
                    0x08000021, stacks: 1, u68: BitConverter.SingleToUInt32Bits((float)(bleedUntil - 82.75)));
                byte[] currentPoisonBuff = InstanceEntities.AddBuff(rescaledGolem.Id,
                    rescaledGolem.Monster.Poisons.Buff!.Value, Game.PoisonBuffRow, 0x08000021,
                    stacks: (ushort)movedPoisonStacks,
                    u68: BitConverter.SingleToUInt32Bits((float)(poisonUntil - 82.75)));
                Assert.Contains(rescaledGolemPackets, packet => packet.SequenceEqual(currentIgniteBuff));
                Assert.Contains(rescaledGolemPackets, packet => packet.SequenceEqual(currentBleedBuff));
                Assert.Contains(rescaledGolemPackets, packet => packet.SequenceEqual(currentPoisonBuff));
                Assert.DoesNotContain(rescaledGolemPackets, packet => packet.Length >= 6
                    && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == oldGolemId
                    && (packet[0], packet[1]) is (0x01, 0xc1));
                var elementalPresentation = Game.MonsterAilmentChangesLocked(resource, 82.75);
                foreach (string kind in new[] { "Chill", "Freeze", "Shock" })
                    Assert.Contains(elementalPresentation, entry => entry.Packet.Length >= 6
                        && BinaryPrimitives.ReadUInt32BigEndian(entry.Packet.AsSpan(2)) == rescaledGolem.Id
                        && entry.Log.Contains(kind, StringComparison.Ordinal));
                hostileTarget = rescaledGolem;
                var taggedB2Actor = rescaledGolem;
                void AssertTaggedB2Current(string boundary)
                {
                    string GolemKind(GolemState.Golem golem) =>
                        $"{golem.Definition.Kind}:0x{golem.Id:x8}";
                    string roster = string.Join(",", resource.Golems.All.Values
                        .OrderBy(GolemKind, StringComparer.Ordinal).Select(GolemKind));
                    string sources = string.Join(",", Game.GemSkillEffects(resource)
                        .Select(grant => $"{Game.SkillsFor(resource).EffectId(grant.Effect)}:0x{grant.Id:x8}"));
                    Assert.True(resource.Golems.All.TryGetValue(taggedB2Actor.Id, out var found),
                        $"{boundary}: tagged B2 actor 0x{taggedB2Actor.Id:x8} is missing; " +
                        $"roster [{roster}], gem sources [{sources}]");
                    var current = found!;
                    Assert.True(ReferenceEquals(taggedB2Actor, current),
                        $"{boundary}: tagged B2 actor 0x{taggedB2Actor.Id:x8} was replaced; " +
                        $"roster [{roster}], gem sources [{sources}]");
                    Assert.True(resourceSource.Id == current.Source,
                        $"{boundary}: tagged B2 actor source changed; roster [{roster}], gem sources [{sources}]");
                    Assert.True(current.GemLevel == 20,
                        $"{boundary}: tagged B2 actor gem level changed; roster [{roster}], gem sources [{sources}]");
                    Assert.True(ReferenceEquals(current.Monster, resource.Monsters[current.Id]),
                        $"{boundary}: tagged B2 actor lost its registered monster; roster [{roster}]");
                    Assert.True(resource.Minions.Owns(current.Id, current.Monster),
                        $"{boundary}: tagged B2 actor lost ownership; roster [{roster}], gem sources [{sources}]");
                    Assert.Contains(Game.GemSkillEffects(resource), grant => grant.Id == resourceSource.Id);
                    Assert.Equal(20, Game.SupportedSkill(resource, resourceSource.Id).Level);
                }
                AssertTaggedB2Current("immediately after actual B2 replacement");
                hostileTarget.Monster.Ignites.Clear();
                hostileTarget.Monster.Ignites.Buff = null;
                hostileTarget.Monster.Bleeds.Clear();
                hostileTarget.Monster.Bleeds.Buff = null;
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Poisons.Buff = null;
                hostileTarget.Monster.Ailments.MoveTo(new MonsterElementalAilments());

                resource.IncomingHitRandom = new HighestRandom();
                hostileAttack = hostileAttack with { Spell = false };
                hostileSource.Attack = hostileAttack;
                resourceSchedule.At = 82.625;
                Assert.False(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    hostileAttack, 82.625));
                Assert.False(hostileTarget.Monster.Bleeds.Burning);
                Assert.False(hostileTarget.Monster.Poisons.Poisoned);
                hostileAttack = hostileAttack with { Spell = true };
                hostileSource.Attack = hostileAttack;
                uint spellBlock = Program.Stats.Key("minion_additional_spell_block_%")!.Value;
                resource.PlayerStats[spellBlock] = 100;
                resource.IncomingHitRandom = new LowestRandom();
                resourceSchedule.At = 82.75;
                Assert.False(Game.GolemTakeHitLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    hostileAttack, 82.75));
                Assert.False(hostileTarget.Monster.Bleeds.Burning);
                Assert.False(hostileTarget.Monster.Poisons.Poisoned);
                resource.PlayerStats.Remove(spellBlock);

                // Prepared family-identity diagnostics: these exercise the owned DoT owner's family guard, not a
                // native reservation lifecycle or an all-family admission proof.
                foreach (var (kind, index) in new[]
                         {
                             ("Agony", 0), ("SkitterCold", 1), ("SkitterLightning", 2),
                         })
                {
                    uint familyId = 0x09000100u + (uint)index;
                    Assert.DoesNotContain(familyId, resource.Golems.All.Keys);
                    Assert.DoesNotContain(familyId, resource.Reservations.Fighters.Keys);
                    Assert.DoesNotContain(familyId, resource.Summons.Fighters.Keys);
                    Assert.DoesNotContain(familyId, resource.Monsters.Keys);
                    var familyMonster = new SpawnedMonster($"prepared {kind} DoT immunity",
                        new(100, 100, 1), 80, 8, 10_000);
                    var family = new GolemState.Golem(familyId, familyMonster,
                        new GolemCatalog.Definition(0, $"diagnostic-{kind}", kind, "", 0, [], false, false, false), 0)
                    {
                        Player = new(Game.CharacterPlayerStats(resource)),
                        LastStep = 82.875,
                    };
                    resource.Monsters[familyId] = familyMonster;
                    resource.Reservations.Fighters[familyId] = family;
                    family.ShieldPool.SetMaximum(500, refill: true);
                    family.ShieldPool.Damage(100, bypass: false, now: 82.875);
                    uint familyShield = family.Shield;
                    uint familyLife = familyMonster.Life;
                    Assert.True(familyMonster.Ignites.Add(82.875, 1_000, .125));
                    Assert.True(familyMonster.Bleeds.Add(82.875, 1_000, .125));
                    Assert.True(familyMonster.Poisons.Add(82.875, 1_000, .125));
                    Game.GolemDotsLocked(resource, family, 82.875, 83);
                    Assert.Equal(familyShield, family.Shield);
                    Assert.Equal(familyLife, familyMonster.Life);
                    Assert.False(family.ShieldPool.Recharging);
                    Assert.True(familyMonster.Ignites.Idle);
                    Assert.True(familyMonster.Bleeds.Idle);
                    Assert.True(familyMonster.Poisons.Idle);
                    resource.Monsters.Remove(familyId);
                    resource.Reservations.Fighters.Remove(familyId);
                }

                AssertTaggedB2Current("before independent Zombie setup");

                // Real persistent Raise Zombie: fresh full cast, JSON round-trip, far-origin restore, source cap changes,
                // legacy zero-ES arrival, dead records, and steady gem-level rescale all cross the production paths.
                var zombieResourceGem = ItemCatalog.Create(
                    Program.Items.Find("Metadata/Items/Gems/SkillGemRaiseZombie")!);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with { Sockets = [.. item.Sockets, new(SocketColour.White, zombieResourceGem)] });
                RecalculateResource();
                var zombieResourceSource = Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "RaiseZombie");
                Assert.Equal(resourceSource.Id, Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonFlameGolem").Id);
                Assert.Equal(20, Game.SupportedSkill(resource, resourceSource.Id).Level);
                Assert.Equal(1, Game.SupportedSkill(resource, zombieResourceSource.Id).Level);
                AssertTaggedB2Current("after original Zombie socket setup");
                var zombieResourceDefinition = Game.SummonData.Find("RaiseZombie")!;
                const uint zombieCorpseId = 0x2200c0de;
                resource.Monsters[zombieCorpseId] = new SpawnedMonster("real zombie corpse", new(100, 100, 0), 70, 8, 0)
                {
                    Hostile = true, Dead = true, Life = 0, Variety = Game.Monsters.Find("Metadata/Monsters/Zombies/Zombie"),
                    Level = 70,
                };
                resource.Summons.Restored = true;
                resourceSchedule.At = 200;
                var freshZombiePackets = await CaptureResource(() => Game.SummonFighter("test", wire.Stream,
                    wire.Ciphers, resource, zombieResourceSource.Id, zombieResourceDefinition, (100, 100), zombieCorpseId));
                var freshZombie = Assert.Single(resource.Summons.Fighters).Value;
                Assert.NotEqual(0u, freshZombie.Id);
                Assert.Equal(freshZombie.Monster.MaxLife, freshZombie.Monster.Life);
                Assert.Equal(freshZombie.MaximumShield, freshZombie.Shield);
                Assert.True(freshZombie.MaximumShield > 0);
                AssertCreateResources(freshZombiePackets, freshZombie.Id, freshZombie.Monster.Life,
                    freshZombie.Shield, freshZombie.Monster.MaxLife, freshZombie.MaximumShield);

                freshZombie.Monster.Life = freshZombie.Monster.MaxLife - 7;
                freshZombie.ShieldPool.Damage(3, bypass: false, now: 200.5);
                freshZombie.Regeneration = .375;
                resourceSchedule.At = 200.75;
                Game.SaveSummons(resource);
                var summonJson = CharacterSaves.ModuleData(resourceCharacter, "Game.SummonedMinions")!;
                var summonSaved = Assert.Single(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(summonJson)!,
                    entry => entry.Effect == "RaiseZombie");
                Assert.NotNull(summonSaved.Resources);
                Assert.Equal(freshZombie.Monster.Life, summonSaved.Resources!.Life);
                Assert.Equal(.375, summonSaved.Resources.LifeRegeneration);
                Assert.Equal(freshZombie.Shield, summonSaved.Resources.EnergyShield.Current);
                Assert.Equal(.25, summonSaved.Resources.EnergyShield.InterruptedSecondsAgo);
                uint steadyLife = summonSaved.Resources.Life;
                uint steadyShield = summonSaved.Resources.EnergyShield.Current;
                string steadyVariety = freshZombie.Monster.Variety!.Id;
                int steadyLevel = freshZombie.Monster.Level;

                int zombieLevelTwenty = Program.Items.Gem(Program.Items.ByHash(zombieResourceGem.TypeHash)!)!.ExperienceFor(20);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == zombieResourceGem.TypeHash
                        ? socket with { Gem = zombieResourceGem with { GemExperience = zombieLevelTwenty } } : socket).ToArray(),
                });
                Assert.Equal(20, Game.SupportedSkill(resource, resourceSource.Id).Level);
                Assert.Equal(20, Game.SupportedSkill(resource, zombieResourceSource.Id).Level);
                AssertTaggedB2Current("after Zombie source reached level 20");
                resourceSchedule.At = 201;
                Task<uint> preparedZombie = null!;
                var zombieRaceGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(zombieRaceGate.Wait(0));
                EnergyShield.Snapshot zombieRaceSnapshot = default;
                try
                {
                    preparedZombie = Game.SummonFighter("test", wire.Stream, wire.Ciphers, resource,
                        zombieResourceSource.Id, zombieResourceDefinition, (100, 100), restoredVariety: steadyVariety,
                        replacingOld: freshZombie);
                    Assert.False(preparedZombie.IsCompleted, "the actual zombie replacement did not reach the held create gate");
                    uint zombieShieldBeforeHit = freshZombie.Shield;
                    uint zombieLifeBeforeHit = freshZombie.Monster.Life;
                    Assert.True(Game.GolemTakeHitLocked(resource, freshZombie, 0x09000033, neverCrit,
                        FixedHit(DamageType.Physical, 1), 201));
                    Assert.True(freshZombie.Monster.Life < zombieLifeBeforeHit || freshZombie.Shield < zombieShieldBeforeHit);
                    zombieRaceSnapshot = freshZombie.ShieldPool.Capture(201);
                }
                finally { zombieRaceGate.Release(); }
                Assert.Equal(0u, await preparedZombie);
                Assert.Same(freshZombie, resource.Summons.Fighters[freshZombie.Id]);
                steadyLife = freshZombie.Monster.Life;
                steadyShield = freshZombie.Shield;
                var steadyPackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At));
                var rescaledZombie = Assert.Single(resource.Summons.Fighters).Value;
                Assert.NotEqual(freshZombie.Id, rescaledZombie.Id);
                Assert.True(rescaledZombie.Monster.Level > steadyLevel);
                Assert.True(rescaledZombie.GemLevel > 1);
                Assert.Equal(steadyVariety, rescaledZombie.Monster.Variety!.Id);
                Assert.Equal(double.PositiveInfinity, resource.Summons.Until[rescaledZombie.Id]);
                Assert.Equal(Math.Min(steadyLife, rescaledZombie.Monster.MaxLife), rescaledZombie.Monster.Life);
                Assert.Equal(Math.Min(steadyShield, rescaledZombie.MaximumShield), rescaledZombie.Shield);
                Assert.Equal(.375, rescaledZombie.Regeneration);
                Assert.Equal(zombieRaceSnapshot.InterruptedSecondsAgo,
                    rescaledZombie.ShieldPool.Capture(201).InterruptedSecondsAgo);
                AssertCreateResources(steadyPackets, rescaledZombie.Id, rescaledZombie.Monster.Life,
                    rescaledZombie.Shield, rescaledZombie.Monster.MaxLife, rescaledZombie.MaximumShield);

                // The same current-state move is exercised through the real SummonsTick rescale path, after actual
                // accepted hit routes admit both the diagnostic ignite and the catalogue bleed/poison rows.
                fireSource.Attack = transferFireAttack;
                hostileSource.Mods = hostileMods;
                hostileSource.Attack = transferHostileAttack;
                resource.IncomingHitRandom = new LowestRandom();
                Assert.True(Game.GolemTakeHitLocked(resource, rescaledZombie, 0x08000021, fireSource,
                    transferFireAttack, 201));
                Assert.True(Game.GolemTakeHitLocked(resource, rescaledZombie, 0x08000021, hostileSource,
                    transferHostileAttack, 201));
                Assert.True(rescaledZombie.Monster.Ignites.Burning);
                Assert.True(rescaledZombie.Monster.Bleeds.Burning);
                Assert.True(rescaledZombie.Monster.Poisons.Poisoned);
                double summonIgniteUntil = rescaledZombie.Monster.Ignites.Until;
                double summonBleedUntil = rescaledZombie.Monster.Bleeds.Until;
                double summonPoisonUntil = rescaledZombie.Monster.Poisons.Until;
                int summonPoisonStacks = rescaledZombie.Monster.Poisons.Stacks(201);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with
                    {
                        Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == zombieResourceGem.TypeHash
                            ? socket with { Gem = zombieResourceGem } : socket).ToArray(),
                    });
                resource.Summons.RefreshAt = 0;
                resourceSchedule.At = 201.125;
                var summonRescalePackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, resource, 201.125));
                Assert.Equal(resourceSource.Id, Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonFlameGolem").Id);
                Assert.Equal(20, Game.SupportedSkill(resource, resourceSource.Id).Level);
                Assert.Equal(1, Game.SupportedSkill(resource, zombieResourceSource.Id).Level);
                AssertTaggedB2Current("after Zombie transfer");
                var transferredZombie = Assert.Single(resource.Summons.Fighters).Value;
                Assert.NotSame(rescaledZombie, transferredZombie);
                Assert.Same(transferredZombie, resource.Summons.Fighters[transferredZombie.Id]);
                Assert.True(transferredZombie.GemLevel > 0);
                uint transferredLife = transferredZombie.Monster.Life;
                uint transferredShield = transferredZombie.Shield;
                Assert.Equal(summonIgniteUntil, transferredZombie.Monster.Ignites.Until);
                Assert.Equal(summonBleedUntil, transferredZombie.Monster.Bleeds.Until);
                Assert.Equal(summonPoisonUntil, transferredZombie.Monster.Poisons.Until);
                Assert.Equal(summonPoisonStacks, transferredZombie.Monster.Poisons.Stacks(201.125));
                Assert.Equal(0x08000021u, transferredZombie.Monster.Ignites.BuffSource);
                Assert.Equal(0x08000021u, transferredZombie.Monster.Bleeds.BuffSource);
                Assert.Equal(0x08000021u, transferredZombie.Monster.Poisons.BuffSource);
                var summonBuffPackets = await CaptureResource(
                    () => Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, resource));
                Assert.Contains(summonBuffPackets, packet => packet.SequenceEqual(InstanceEntities.AddBuff(
                    transferredZombie.Id, transferredZombie.Monster.Ignites.Buff!.Value, Game.IgnitedBuffRow,
                    0x08000021, stacks: 1,
                    u68: BitConverter.SingleToUInt32Bits((float)(summonIgniteUntil - 201.125)))));
                Assert.Contains(summonBuffPackets, packet => packet.SequenceEqual(InstanceEntities.AddBuff(
                    transferredZombie.Id, transferredZombie.Monster.Bleeds.Buff!.Value,
                    LabyrinthTrials.BleedingBuffRow!.Value, 0x08000021,
                    stacks: 1,
                    u68: BitConverter.SingleToUInt32Bits((float)(summonBleedUntil - 201.125)))));
                Assert.Contains(summonBuffPackets, packet => packet.SequenceEqual(InstanceEntities.AddBuff(
                    transferredZombie.Id, transferredZombie.Monster.Poisons.Buff!.Value, Game.PoisonBuffRow,
                    0x08000021, stacks: (ushort)summonPoisonStacks,
                    u68: BitConverter.SingleToUInt32Bits((float)(summonPoisonUntil - 201.125)))));
                Assert.DoesNotContain(summonBuffPackets, packet => packet.Length >= 6
                    && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(2)) == rescaledZombie.Id
                    && (packet[0], packet[1]) is (0x01, 0xc1));
                AssertCreateResources(summonRescalePackets, transferredZombie.Id, transferredZombie.Monster.Life,
                    transferredZombie.Shield, transferredZombie.Monster.MaxLife, transferredZombie.MaximumShield);
                rescaledZombie = transferredZombie;

                // Clear the moved-status fixture, then restore the actual socketed gem to level 20 and cross the
                // real rescale path so the existing high/low cap controls still start from a genuine high actor.
                rescaledZombie.Monster.Ignites.Clear();
                rescaledZombie.Monster.Ignites.Buff = null;
                rescaledZombie.Monster.Bleeds.Clear();
                rescaledZombie.Monster.Bleeds.Buff = null;
                rescaledZombie.Monster.Poisons.Clear();
                rescaledZombie.Monster.Poisons.Buff = null;
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == zombieResourceGem.TypeHash
                        ? socket with { Gem = zombieResourceGem with { GemExperience = zombieLevelTwenty } } : socket).ToArray(),
                });
                resource.Summons.RefreshAt = 0;
                resourceSchedule.At = 201.25;
                var restoredHighPackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, resource, 201.25));
                var restoredHighZombie = Assert.Single(resource.Summons.Fighters).Value;
                Assert.NotSame(transferredZombie, restoredHighZombie);
                Assert.True(restoredHighZombie.Monster.Level > transferredZombie.Monster.Level);
                Assert.True(restoredHighZombie.GemLevel > transferredZombie.GemLevel);
                Assert.Equal(transferredLife, restoredHighZombie.Monster.Life);
                Assert.Equal(transferredShield, restoredHighZombie.Shield);
                AssertCreateResources(restoredHighPackets, restoredHighZombie.Id, restoredHighZombie.Monster.Life,
                    restoredHighZombie.Shield, restoredHighZombie.Monster.MaxLife, restoredHighZombie.MaximumShield);
                rescaledZombie = restoredHighZombie;
                Assert.Equal(20, Game.SupportedSkill(resource, zombieResourceSource.Id).Level);
                AssertTaggedB2Current("before spirit control");

                rescaledZombie.Monster.Life = rescaledZombie.Monster.MaxLife;
                rescaledZombie.ShieldPool.SetMaximum(rescaledZombie.MaximumShield, refill: true);
                resourceSchedule.At = 201.5;
                Game.SaveSummons(resource);
                uint highLife = rescaledZombie.Monster.Life;
                uint highShield = rescaledZombie.Shield;
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == zombieResourceGem.TypeHash
                        ? socket with { Gem = zombieResourceGem } : socket).ToArray(),
                });
                var lowerClient = NewResourceClient("integer-resource-zombie-lower", 7000);
                var lowerPackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, lowerClient, 7000));
                var lowerZombie = Assert.Single(lowerClient.Summons.Fighters).Value;
                Assert.True(highLife > lowerZombie.Monster.MaxLife);
                Assert.True(highShield > lowerZombie.MaximumShield);
                Assert.Equal(lowerZombie.Monster.MaxLife, lowerZombie.Monster.Life);
                Assert.Equal(lowerZombie.MaximumShield, lowerZombie.Shield);
                AssertCreateResources(lowerPackets, lowerZombie.Id, lowerZombie.Monster.Life,
                    lowerZombie.Shield, lowerZombie.Monster.MaxLife, lowerZombie.MaximumShield);

                lowerZombie.Monster.Life = lowerZombie.Monster.MaxLife - 5;
                lowerZombie.ShieldPool.Damage(2, bypass: false, now: 7000.5);
                resourceSchedule.At = 7000.75;
                Game.SaveSummons(lowerClient);
                uint increasedLife = lowerZombie.Monster.Life;
                uint increasedShield = lowerZombie.Shield;
                var increasedJson = CharacterSaves.ModuleData(resourceCharacter, "Game.SummonedMinions")!;
                var increasedSaved = Assert.Single(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(increasedJson)!,
                    entry => entry.Effect == "RaiseZombie");
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = item.Sockets.Select(socket => socket.Gem?.TypeHash == zombieResourceGem.TypeHash
                        ? socket with { Gem = zombieResourceGem with { GemExperience = zombieLevelTwenty } } : socket).ToArray(),
                });
                var higherClient = NewResourceClient("integer-resource-zombie-higher", 9000);
                var higherPackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, higherClient, 9000));
                var higherZombie = Assert.Single(higherClient.Summons.Fighters).Value;
                Assert.True(higherZombie.Monster.MaxLife > lowerZombie.Monster.MaxLife);
                Assert.True(higherZombie.MaximumShield > lowerZombie.MaximumShield);
                Assert.Equal(increasedLife, higherZombie.Monster.Life);
                Assert.Equal(increasedShield, higherZombie.Shield);
                Assert.Equal(.25, higherZombie.ShieldPool.Capture(9000).InterruptedSecondsAgo);
                AssertCreateResources(higherPackets, higherZombie.Id, higherZombie.Monster.Life,
                    higherZombie.Shield, higherZombie.Monster.MaxLife, higherZombie.MaximumShield);
                await Game.SummonsTick("test", wire.Stream, wire.Ciphers, higherClient, 9000);
                Assert.Equal(increasedLife, higherZombie.Monster.Life);
                Assert.Equal(increasedShield, higherZombie.Shield);
                Game.SaveSummons(higherClient);
                var repeatedSummon = Assert.Single(JsonSerializer.Deserialize<SummonedMinions.Saved[]>(
                    CharacterSaves.ModuleData(resourceCharacter, "Game.SummonedMinions")!)!, entry => entry.Effect == "RaiseZombie");
                Assert.Equal(increasedSaved.Resources!.Life, repeatedSummon.Resources!.Life);
                Assert.Equal(increasedSaved.Resources.EnergyShield.Current, repeatedSummon.Resources.EnergyShield.Current);
                Assert.Equal(increasedSaved.Resources.EnergyShield.Fraction, repeatedSummon.Resources.EnergyShield.Fraction);

                string legacySummonJson = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Effect = "RaiseZombie", Source = zombieResourceSource.Id, Variety = steadyVariety, Life = .42,
                    },
                });
                Assert.DoesNotContain("\"Resources\"", legacySummonJson);
                CharacterSaves.ChangeModuleData(resourceCharacter, "Game.SummonedMinions", _ => legacySummonJson);
                resourceSchedule.At = 10_000;
                var legacySummon = NewResourceClient("integer-resource-zombie-legacy", resourceSchedule.At);
                var legacySummonPackets = await CaptureResource(
                    () => Game.SummonsTick("test", wire.Stream, wire.Ciphers, legacySummon, resourceSchedule.At));
                var legacyZombie = Assert.Single(legacySummon.Summons.Fighters).Value;
                Assert.Equal((uint)(legacyZombie.Monster.MaxLife * .42), legacyZombie.Monster.Life);
                Assert.Equal(0u, legacyZombie.Shield);
                Assert.Equal(0d, legacyZombie.ShieldPool.Capture(resourceSchedule.At).InterruptedSecondsAgo);
                AssertCreateResources(legacySummonPackets, legacyZombie.Id, legacyZombie.Monster.Life, 0,
                    legacyZombie.Monster.MaxLife, legacyZombie.MaximumShield);
                double legacySummonDelay = EnergyShield.Delay(name => legacyZombie.Monster.Mods.Effects[name]);
                resourceSchedule.At = 10_000 + legacySummonDelay - .01;
                await Game.SummonsTick("test", wire.Stream, wire.Ciphers, legacySummon, resourceSchedule.At);
                Assert.Equal(0u, legacyZombie.Shield);
                resourceSchedule.At = 10_000 + legacySummonDelay + .1;
                await Game.SummonsTick("test", wire.Stream, wire.Ciphers, legacySummon, resourceSchedule.At);
                Assert.InRange(legacyZombie.Shield, 1u, legacyZombie.MaximumShield - 1);

                var spiritGem = ItemCatalog.Create(
                    Program.Items.Find("Metadata/Items/Gems/SkillGemSummonRagingSpirit")!);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with { Sockets = [.. item.Sockets, new ItemSocket(SocketColour.White, spiritGem)] });
                RecalculateResource();
                var spiritSource = Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonRagingSpirit");
                var spiritDefinition = Game.SummonData.Find("SummonRagingSpirit")!;
                resourceSchedule.At = 11_000;
                uint spiritId = await Game.SummonFighter("test", wire.Stream, wire.Ciphers, resource,
                    spiritSource.Id, spiritDefinition, (100, 100));
                var spirit = resource.Summons.Fighters[spiritId];
                double spiritUntil = resource.Summons.Until[spiritId];
                Assert.True(double.IsFinite(spiritUntil));
                AssertTaggedB2Current("before spirit expiry");
                resourceSchedule.At = spiritUntil - .25;
                var spiritGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                Assert.True(spiritGate.Wait(0));
                var preparedSpirit = Game.SummonFighter("test", wire.Stream, wire.Ciphers, resource,
                    spiritSource.Id, spiritDefinition, (100, 100), replacingOld: spirit);
                Assert.False(preparedSpirit.IsCompleted);
                resourceSchedule.At = spiritUntil + .25;
                spiritGate.Release();
                Assert.Equal(0u, await preparedSpirit);
                Assert.Same(spirit, resource.Summons.Fighters[spiritId]);
                Assert.Equal(spiritUntil, resource.Summons.Until[spiritId]);
                await Game.SummonsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                Assert.DoesNotContain(spiritId, resource.Summons.Fighters.Keys);
                AssertTaggedB2Current("after spirit expiry");
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with
                    {
                        Sockets = item.Sockets.Where(socket => socket.Gem?.TypeHash != spiritGem.TypeHash).ToArray(),
                    });
                RecalculateResource();

                CharacterSaves.ChangeModuleData(resourceCharacter, "Game.SummonedMinions", _ => JsonSerializer.Serialize(new[]
                {
                    new SummonedMinions.Saved("RaiseZombie", zombieResourceSource.Id, steadyVariety, 0),
                    new SummonedMinions.Saved("RaiseZombie", zombieResourceSource.Id, steadyVariety, 0,
                        Resources: new GolemState.MinionResourceSnapshot(0, 0, new(0, 0, 0))),
                }));
                resourceSchedule.At = 12_000;
                var deadSummon = NewResourceClient("integer-resource-zombie-dead", resourceSchedule.At);
                await Game.SummonsTick("test", wire.Stream, wire.Ciphers, deadSummon, resourceSchedule.At);
                Assert.Empty(deadSummon.Summons.Fighters);
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id, item => item with
                {
                    Sockets = [new(SocketColour.White, resourceGem with { GemExperience = levelTwenty })],
                });
                RecalculateResource();
                AssertTaggedB2Current("before generic 83 and tail 84 controls");
                var highTailSource = Assert.Single(Game.GemSkillEffects(resource),
                    grant => Game.SkillsFor(resource).EffectId(grant.Effect) == "SummonFlameGolem");
                Assert.Equal(resourceSource.Id, highTailSource.Id);
                Assert.Equal(20, Game.SupportedSkill(resource, resourceSource.Id).Level);
                var highTailActor = Assert.Single(resource.Golems.All.Values,
                    golem => golem.Monster.Poisons.BuffSource == 0x08000021);
                Assert.Equal(resourceDefinition.Effect, highTailActor.Definition.Effect);
                Assert.Equal(resourceSource.Id, highTailActor.Source);
                Assert.Equal(20, highTailActor.GemLevel);
                hostileTarget = highTailActor;
                hostileId = highTailActor.Id;
                Assert.Same(highTailActor.Monster, resource.Monsters[hostileId]);
                Assert.True(resource.Minions.Owns(hostileId, highTailActor.Monster));
                resourceSchedule.At = 83;

                // Generic hostile loops skip the exact owned actor and cannot consume its sets or manufacture rewards.
                // The production projectile session supplies the same explicit binary clock to those loops.
                var genericProjectiles = new global::Game.ProjectileSession("test", wire.Stream, wire.Ciphers, resource);
                resource.ProjectileRuntime = genericProjectiles;
                try
                {
                    genericProjectiles.AdvanceTo(global::GameServer.Core.Projectiles.ProjectileRuntime.Time(83));
                    hostileTarget.Monster.Ignites.Clear();
                    hostileTarget.Monster.Bleeds.Clear();
                    hostileTarget.Monster.Poisons.Clear();
                    hostileTarget.Monster.Ignites.Buff = null;
                    hostileTarget.Monster.Bleeds.Buff = null;
                    hostileTarget.Monster.Poisons.Buff = null;
                    hostileTarget.Monster.Ignites.BuffSource = 0;
                    hostileTarget.Monster.Bleeds.BuffSource = 0;
                    hostileTarget.Monster.Poisons.BuffSource = 0;
                    hostileTarget.LastStep = 83;
                    Assert.True(hostileTarget.Monster.Ignites.Add(83, 1000, 2));
                    Assert.True(hostileTarget.Monster.Bleeds.Add(83, 1000, 2));
                    Assert.True(hostileTarget.Monster.Poisons.Add(83, 1000, 2));
                    uint ownedShield = hostileTarget.Shield;
                    uint ownedLife = hostileTarget.Monster.Life;
                    genericProjectiles.AdvanceTo(global::GameServer.Core.Projectiles.ProjectileRuntime.Time(83.125));
                    resourceSchedule.At = 83.125;
                    await Game.BurnIgnited("test", wire.Stream, wire.Ciphers, resource);
                    await Game.BleedMonsters("test", wire.Stream, wire.Ciphers, resource);
                    await Game.TickPoisons("test", wire.Stream, wire.Ciphers, resource);
                    Assert.Equal(ownedShield, hostileTarget.Shield);
                    Assert.Equal(ownedLife, hostileTarget.Monster.Life);
                    genericProjectiles.AdvanceTo(global::GameServer.Core.Projectiles.ProjectileRuntime.Time(83.25));
                    resourceSchedule.At = 83.25;
                    await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                    Assert.True(hostileTarget.Shield < ownedShield || hostileTarget.Monster.Life < ownedLife);

                    uint nonOwnedId = 0x09000022;
                    Assert.DoesNotContain(nonOwnedId, resource.Golems.All.Keys);
                    Assert.DoesNotContain(nonOwnedId, resource.Reservations.Fighters.Keys);
                    Assert.DoesNotContain(nonOwnedId, resource.Summons.Fighters.Keys);
                    Assert.DoesNotContain(nonOwnedId, resource.Monsters.Keys);
                    var nonOwned = new SpawnedMonster("nonowned dot baseline", new(100, 100, 0), 100, 8, 1_000_000)
                    {
                        Variety = rhoa,
                    };
                    resource.Monsters[nonOwnedId] = nonOwned;
                    Assert.True(nonOwned.Ignites.Add(83.25, 1000, 1));
                    Assert.True(nonOwned.Bleeds.Add(83.25, 1000, 1));
                    Assert.True(nonOwned.Poisons.Add(83.25, 1000, 1));
                    uint nonOwnedLife = nonOwned.Life;
                    genericProjectiles.AdvanceTo(global::GameServer.Core.Projectiles.ProjectileRuntime.Time(83.5));
                    resourceSchedule.At = 83.5;
                    await Game.BurnIgnited("test", wire.Stream, wire.Ciphers, resource);
                    await Game.BleedMonsters("test", wire.Stream, wire.Ciphers, resource);
                    await Game.TickPoisons("test", wire.Stream, wire.Ciphers, resource);
                    Assert.True(nonOwned.Life < nonOwnedLife);
                    resource.Monsters.Remove(nonOwnedId);
                }
                finally
                {
                    genericProjectiles.Invalidate();
                    await genericProjectiles.OutputCompleted;
                    resource.ProjectileRuntime = null;
                }

                // A held send gate cannot emit an old actor's queued buff after that exact actor is replaced. The same
                // packet waits normally while the current actor still owns its id.
                resourceSchedule.At = 83.75;
                await Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, resource);
                hostileTarget.Monster.Dead = false;
                hostileTarget.Monster.Life = hostileTarget.Monster.MaxLife;
                hostileTarget.ShieldPool.SetMaximum(hostileTarget.MaximumShield, refill: true);
                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Poisons.Buff = null;
                hostileTarget.Monster.Poisons.BuffSource = 0;
                Assert.True(hostileTarget.Monster.Poisons.Add(84, 10, 2));
                Game.GolemDotsLocked(resource, hostileTarget, 84, 84.125);
                Assert.True(hostileTarget.Monster.Poisons.Buff.HasValue);
                ushort queuedPoisonBuff = hostileTarget.Monster.Poisons.Buff.Value;
                byte[] queuedPoisonPacket = InstanceEntities.AddBuff(hostileId, queuedPoisonBuff, Game.PoisonBuffRow,
                    Program.PlayerId, stacks: 1,
                    u68: BitConverter.SingleToUInt32Bits((float)(hostileTarget.Monster.Poisons.Until - 84.125)));
                var queuedPoison = Assert.Single(resource.Minions.OutputQueue,
                    output => output.TargetId == hostileId && ReferenceEquals(output.Target, hostileTarget.Monster)
                        && output.Packet.SequenceEqual(queuedPoisonPacket));
                Assert.NotNull(queuedPoison);

                var replacementMonster = new SpawnedMonster("replacement at the held gate", new(100, 100, 1),
                    100, 8, 1_000_000);
                var staleBuffPackets = await CaptureResource(async () =>
                {
                    var sendGate = Program.SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
                    Assert.True(sendGate.Wait(0));
                    resource.Monsters[hostileId] = replacementMonster;
                    var publishing = Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, resource);
                    Assert.False(publishing.IsCompleted);
                    sendGate.Release();
                    await publishing;
                    resource.Monsters[hostileId] = hostileTarget.Monster;
                });
                Assert.DoesNotContain(staleBuffPackets, packet => packet.SequenceEqual(queuedPoisonPacket));

                hostileTarget.Monster.Poisons.Clear();
                hostileTarget.Monster.Poisons.BuffSource = 0;
                Game.GolemDotsLocked(resource, hostileTarget, 84.125, 84.25);
                byte[] validRemoval = InstanceEntities.RemoveBuff(hostileId, queuedPoisonBuff);
                Assert.Single(resource.Minions.OutputQueue,
                    output => output.TargetId == hostileId && ReferenceEquals(output.Target, hostileTarget.Monster)
                        && output.Packet.SequenceEqual(validRemoval));
                var validBuffPackets = await CaptureResource(
                    () => Game.PublishMinionOutcomes("test", wire.Stream, wire.Ciphers, resource));
                Assert.Contains(validBuffPackets, packet => packet.SequenceEqual(validRemoval));

                // Existing Life regeneration is applied before owned DoT. A low-Life golem with real Flame regeneration
                // survives this frame, while the positive stationary bleed still prevents ES recharge.
                resourceInventory.Change(PlayerInventories.Weapon1, weaponSlot.Id,
                    item => item with { Sockets = [new(SocketColour.White, resourceGem)] });
                RecalculateResource();
                var regenerationKey = Program.Stats.Key("minion_life_regeneration_rate_per_minute_%")!.Value;
                resource.PlayerStats.Remove(regenerationKey);
                resource.PlayerStats[armourKey] = 0;
                RecalculateResource();
                resourceSchedule.At = 84.25;
                uint lowRegenerationId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100));
                var lowRegeneration = resource.Golems.All[lowRegenerationId];
                Assert.Equal(663u, lowRegeneration.Monster.MaxLife);
                uint golemArmourKey = Program.Stats.Key("golem_physical_damage_reduction_rating")!.Value;
                int ownerArmour = lowRegeneration.Player.GetValueOrDefault(armourKey);
                int innateGolemArmour = lowRegeneration.Player.GetValueOrDefault(golemArmourKey);
                Assert.Equal(0, ownerArmour);
                Assert.Equal(0, innateGolemArmour);
                double lowRegenerationArmour = Math.Max(0, ownerArmour + innateGolemArmour
                    + lowRegeneration.Monster.Mods.Effects["base_physical_damage_reduction_rating"])
                    * Math.Max(0,
                        1 + lowRegeneration.Monster.Mods.Effects["determination_aura_armour_+%_final"] / 100.0);
                Assert.Equal(0, lowRegenerationArmour, 12);
                Assert.Equal(0, lowRegeneration.Monster.Mods.Effects.PhysicalReduction);
                Assert.Equal(0, Game.MonsterHitResistance(lowRegeneration.Monster, DamageType.Physical, default).Value);
                uint lowRegenerationLife = lowRegeneration.Monster.Life;
                uint lowRegenerationShield = lowRegeneration.Shield;
                int lowLifeHit = checked((int)(lowRegenerationLife + lowRegenerationShield - 1));
                Assert.True(Game.GolemTakeHitLocked(resource, lowRegeneration, 0x08000023, neverCrit,
                    FixedHit(DamageType.Physical, lowLifeHit), 84.25));
                Assert.Equal(1u, lowRegeneration.Monster.Life);
                Assert.Equal(0u, lowRegeneration.Shield);

                lowRegeneration.Monster.Walk.Order(lowRegeneration.Monster.Walk.Position(84.25).X,
                    lowRegeneration.Monster.Walk.Position(84.25).Y, 84.25);
                // Labelled diagnostic: a stationary 4 DPS bleed for one second isolates registration order after
                // the real low-Life hit, without a moving multiplier or an ordinary attack's on-hit effects.
                Assert.True(lowRegeneration.Monster.Bleeds.Add(84.25, 4, 1));
                resourceSchedule.At = 84.75;
                Assert.False(lowRegeneration.Monster.Walk.Moving(resourceSchedule.At));
                Assert.Equal(1, Game.BleedTaken(lowRegeneration.Monster, resourceSchedule.At), 12);
                uint expectedRegeneration = (uint)Math.Floor(lowRegeneration.Monster.MaxLife * .01 * .5);
                Assert.Equal(3u, expectedRegeneration);
                int bleedOutputStart = resource.Minions.OutputQueue.Count;
                await Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At);
                var lowRegenerationOutputs = resource.Minions.OutputQueue.Skip(bleedOutputStart).ToArray();
                Assert.False(lowRegeneration.Monster.Dead);
                Assert.Equal(1u + expectedRegeneration - 2u, lowRegeneration.Monster.Life);
                Assert.Equal(2u, lowRegeneration.Monster.Life);
                Assert.Equal(0u, lowRegeneration.Shield);
                Assert.False(lowRegeneration.ShieldPool.Recharging);
                Assert.DoesNotContain(lowRegenerationOutputs, output => output.Kind == PlayerMinions.OutputKind.Flinch);
                Assert.DoesNotContain(lowRegenerationOutputs,
                    output => output.Log is "golem takes damage" or "golem died");

                resource.PlayerStats[regenerationKey] = -60;
                resource.PlayerStats.Remove(armourKey);
                RecalculateResource();

                // Fatal stat conversion removes every native owned ailment buff, empties the pools and cannot resurrect.
                resource.PlayerStats.Remove(spellBlock);
                resourceSchedule.At = 85;
                hostileId = await Game.SummonGolem("test", wire.Stream, wire.Ciphers, resource,
                    resourceSource.Id, resourceDefinition, (100, 100));
                hostileTarget = resource.Golems.All[hostileId];
                resource.IncomingHitRandom = new LowestRandom();
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, fireSource,
                    DamagePacket.Of(DamageType.Fire, 1, 1), critical: false, attack: true, 85);
                Assert.True(hostileTarget.Monster.Ignites.Burning);
                Assert.True(hostileTarget.Monster.Ignites.Buff.HasValue);
                Game.GolemHostileAilmentsLocked(resource, hostileTarget, 0x08000021, hostileSource,
                    DamagePacket.Of(DamageType.Physical, 1, 1), critical: false, attack: true, 85);
                Assert.True(hostileTarget.Monster.Bleeds.Burning);
                Assert.True(hostileTarget.Monster.Poisons.Poisoned);
                Assert.True(hostileTarget.Monster.Bleeds.Buff.HasValue);
                Assert.True(hostileTarget.Monster.Poisons.Buff.HasValue);
                ushort[] dyingBuffs = [hostileTarget.Monster.Ignites.Buff.Value,
                    hostileTarget.Monster.Bleeds.Buff.Value, hostileTarget.Monster.Poisons.Buff.Value];
                Assert.Equal(3, dyingBuffs.Distinct().Count());
                uint fatalChaosResistance = Program.Stats.Key("minion_chaos_resistance_%")!.Value;
                bool hadFatalChaosResistance = resource.PlayerStats.Remove(fatalChaosResistance,
                    out int previousFatalChaosResistance);
                if (hadFatalChaosResistance) _ = Game.CharacterPlayerStats(resource);
                resource.PlayerStats[fatalChaosResistance] = 30;
                var fatalPlayer = Game.CharacterPlayerStats(resource);
                var fatalMods = Game.GolemModifiers(resource, hostileTarget, fatalPlayer);
                Assert.Equal(30, fatalMods.Effects.Resistance(DamageType.Chaos));
                var fatalPools = Game.GolemPools.Calculate(Game.Monsters, hostileTarget.Monster.Variety!,
                    hostileTarget.Monster.Level, hostileTarget, fatalPlayer,
                    fatalMods.Effects);
                Assert.Equal(50, fatalPools.ChaosResistance);
                Assert.Equal(100, fatalPools.ConversionPercent);
                Assert.True(fatalPools.Fatal);
                Assert.Equal(0, fatalPools.MaximumEnergyShield);
                Assert.Equal(0u, fatalPools.MaximumLife);
                resourceSchedule.At = 85.125;
                var deathPackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At));
                Assert.True(hostileTarget.Monster.Dead);
                Assert.Equal(0u, hostileTarget.Monster.Life);
                Assert.Equal(0u, hostileTarget.Shield);
                Assert.Equal(0u, hostileTarget.MaximumShield);
                Assert.Equal(1u, hostileTarget.Monster.MaxLife);
                Assert.True(hostileTarget.Monster.Ignites.Idle);
                Assert.True(hostileTarget.Monster.Bleeds.Idle);
                Assert.True(hostileTarget.Monster.Poisons.Idle);
                Assert.Null(hostileTarget.Monster.Ignites.Buff);
                Assert.Null(hostileTarget.Monster.Bleeds.Buff);
                Assert.Null(hostileTarget.Monster.Poisons.Buff);
                Assert.Equal(87.125, hostileTarget.ReviveAt);
                byte[] deathPacket = InstanceProtocol.LifeUpdate(hostileTarget.Id, 0);
                Assert.Single(deathPackets, packet => packet.AsSpan().IndexOf(deathPacket) >= 0);
                foreach (ushort buff in dyingBuffs)
                {
                    byte[] removed = InstanceEntities.RemoveBuff(hostileTarget.Id, buff);
                    Assert.Single(deathPackets, packet => packet.SequenceEqual(removed));
                }

                resourceSchedule.At = 85.25;
                var repeatedDeathPackets = await CaptureResource(
                    () => Game.GolemsTick("test", wire.Stream, wire.Ciphers, resource, resourceSchedule.At));
                Assert.Equal(87.125, hostileTarget.ReviveAt);
                Assert.Equal(0u, hostileTarget.Monster.Life);
                Assert.DoesNotContain(repeatedDeathPackets, packet => packet.AsSpan().IndexOf(deathPacket) >= 0);
                Assert.DoesNotContain(repeatedDeathPackets, packet => dyingBuffs.Any(buff =>
                    packet.SequenceEqual(InstanceEntities.RemoveBuff(hostileTarget.Id, buff))));

                var currentWeapon = resourceInventory.Items(PlayerInventories.Weapon1).Single();
                Assert.Equal(weaponSlot.Id, currentWeapon.Id);
                Assert.Equal(resourceGem, currentWeapon.Item.Sockets.Single().Gem);
                Assert.Equal(armourSlot.Id, resourceInventory.Items(PlayerInventories.BodyArmour).Single().Id);
                resourceInventory.Remove(PlayerInventories.Weapon1, weaponSlot.Id);
                RecalculateResource();
                resource.Golems.All.Remove(hostileId);
                resource.Minions.Remove(hostileId);
                resource.Monsters.Remove(hostileId);
                Assert.DoesNotContain(hostileId, resource.Golems.All.Keys);
                Assert.True(hostileTarget.Monster.Ignites.Add(85.625, 10, 2));
                uint staleShield = hostileTarget.Shield;
                uint staleLife = hostileTarget.Monster.Life;
                Game.GolemStepLocked("test", wire.Stream, wire.Ciphers, resource, hostileTarget, 85.75);
                Assert.Equal(staleShield, hostileTarget.Shield);
                Assert.Equal(staleLife, hostileTarget.Monster.Life);
                if (hadFatalChaosResistance) resource.PlayerStats[fatalChaosResistance] = previousFatalChaosResistance;
                else resource.PlayerStats.Remove(fatalChaosResistance);

                resource.IncomingHitRandom = Random.Shared;
            }
            finally
            {
                global::Game.AreaUpdateContext.Value = previousResourceContext;
                foreach (var client in extraResourceClients) client.Closed = true;
                if (resource is not null)
                {
                    resource.Closed = true;
                    PlayerInventories.Forget(resourceCharacter);
                    Game.InactiveItems.TryRemove(resourceCharacter, out _);
                }
            }
        }
        finally
        {
            conn.Closed = true; PlayerInventories.Forget(conn.Character!);
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!); TestEnvironment.ReleaseSaves(path);
        }
    }

    sealed class HighestRandom : Random
    {
        public override int Next(int maxValue) => maxValue > 0 ? maxValue - 1 : 0;
        public override int Next(int minValue, int maxValue) => maxValue > minValue ? maxValue - 1 : minValue;
    }

    sealed class LowestRandom : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
    }

    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(); readonly TcpClient reader;
        readonly Task drain;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire()
        {
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
            drain = Task.Run(async () =>
            {
                try { var buffer = new byte[65536]; while (await reader.GetStream().ReadAsync(buffer) > 0) { } }
                catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            });
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); drain.GetAwaiter().GetResult(); }
    }

    sealed class AreaClockSchedule : IPlayerUpdateSchedule
    {
        public double At;
        public double Now(ClientState conn) => At;
        public Task Wait(int milliseconds, CancellationToken stopping) => Task.CompletedTask;
    }

    // Production cast loops use a completed wait to re-check time. This fixture clock advances the simulated
    // deadline on each wait so they terminate deterministically without sleeps, Task.Delay or a concurrent setter.
    sealed class AdvancingAreaClockSchedule : IPlayerUpdateSchedule
    {
        public double At;
        public double Now(ClientState conn) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }

    // Holds production release work after the paid output. The first wait is a deterministic task source; the caller
    // takes the send gate before resuming so the next mutation is the final placement send.
    sealed class HeldAfterFirstAreaWaitSchedule : IPlayerUpdateSchedule
    {
        public double At;
        public readonly TaskCompletionSource Entered = new();
        readonly TaskCompletionSource held = new();
        int waits;
        public double Now(ClientState conn) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            if (Interlocked.Increment(ref waits) > 1)
            {
                At += milliseconds / 1000d;
                return Task.CompletedTask;
            }
            Entered.TrySetResult();
            return held.Task;
        }
        public void Resume() => held.TrySetResult();
    }

    sealed class OneResourceStepSchedule(double start, Action resourceAt, Action stop) : IPlayerUpdateSchedule
    {
        int waits;
        public double Now(ClientState conn) => start;
        public double ResourceNow(ClientState conn)
        {
            resourceAt();
            return start + 1;
        }
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            if (waits++ == 0) return Task.CompletedTask;
            stop();
            return Task.CompletedTask;
        }
    }
}
