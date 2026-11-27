using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using GameServer.Modules.Game;
using GameServer.Modules.Mercenary;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class MercenaryFullFlowTests
{
    static Item Make(string baseMetadataPath, ItemRarity rarity = ItemRarity.Rare, bool identified = true, byte? reqLevel = null) =>
        ItemCatalog.Create(Program.Items.Find(baseMetadataPath) ?? throw new InvalidOperationException($"Missing {baseMetadataPath}"))
            with { Rarity = rarity, Identified = identified, RequiredLevel = reqLevel };

    static IModuleContext Context(IModuleLifetime lifetime)
    {
        var context = System.Reflection.DispatchProxy.Create<IModuleContext, MercenaryTests.Hook>();
        ((MercenaryTests.Hook)(object)context).Lifetime = lifetime;
        return context;
    }

    [Fact]
    public void CanMercenaryEquipItem_rules_ExtraStats_curve_and_ComputeStats_match_329_spec()
    {
        MercenaryModule.LoadData();
        TestEnvironment.OpenOwnSaves("merc-equip-rules");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameLogic(lifetime));
        var league = new League("Standard", "Standard", false, false);
        var player = new Player(new(1, "1_1_town", 200, 200, 50, 50, null, []), 1, 1)
        {
            Character = { Name = "EqTest" + Guid.NewGuid().ToString("N")[..8], ClassRow = 2, League = "Standard", Experience = CharacterStats.LevelStarts[90] },
            Pools = new(90, 3000, 1000, 0, 0, Strength: 200, Dexterity: 200, Intelligence: 200),
            LeagueModule = new(league, new HashSet<string>(["Game", "Mercenary"])),
        };
        try
        {
            // Find representative builds across offhand modes (0 = 2H only, 1/2 = dual-wield, 3 = Shield/Quiver only) and Str/Int attributes.
            var strClass = MercenaryTables.Classes.First(c => c.Attribute == "Str" && MercenaryTables.BuildsOf(c.Row).Count > 0);
            var intClass = MercenaryTables.Classes.First(c => c.Attribute == "Int" && MercenaryTables.BuildsOf(c.Row).Count > 0);
            var mode0Build = MercenaryTables.Builds.First(b => b.OffhandMode == 0 && MercenaryTables.ClassOf(b.ClassRow) is not null);
            var mode3ShieldBuild = MercenaryTables.Builds.First(b => b.OffhandMode == 3
                && b.WieldableItemClasses.Contains(MercenaryTables.ShieldClassRow)
                && MercenaryTables.ClassOf(b.ClassRow) is not null);
            var strBuild = MercenaryTables.BuildsOf(strClass.Row)[0];

            // 1. Temporary mercenary gear is always locked.
            var rareRing = Make("Metadata/Items/Rings/Ring1", ItemRarity.Rare, identified: true, reqLevel: 10);
            Assert.NotNull(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: false, PlayerInventories.Ring1, rareRing));

            // 2. Unidentified items are always rejected.
            var unidRing = rareRing with { Identified = false };
            Assert.NotNull(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: true, PlayerInventories.Ring1, unidRing));
            Assert.Null(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: true, PlayerInventories.Ring1, rareRing));

            // 3. Level requirement: floor(reqLevel * 70 / 100) <= mercLevel (req 70 -> eff 49; req 84 -> eff 58).
            var lvl70Ring = rareRing with { RequiredLevel = 70 };
            Assert.Null(MercenaryEquipment.CanEquip(player, strClass, strBuild, mercLevel: 49, isPermanent: true, PlayerInventories.Ring1, lvl70Ring));
            Assert.NotNull(MercenaryEquipment.CanEquip(player, strClass, strBuild, mercLevel: 48, isPermanent: true, PlayerInventories.Ring1, lvl70Ring));

            // 4. Armour attribute tag check: Str mercenary accepts str_armour, rejects pure int_armour.
            var strBody = Make("Metadata/Items/Armours/BodyArmours/BodyStr1", ItemRarity.Rare, reqLevel: 10);
            var intBody = Make("Metadata/Items/Armours/BodyArmours/BodyInt1", ItemRarity.Rare, reqLevel: 10);
            Assert.Null(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: true, PlayerInventories.BodyArmour, strBody));
            Assert.NotNull(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: true, PlayerInventories.BodyArmour, intBody));
            Assert.Null(MercenaryEquipment.CanEquip(player, intClass, MercenaryTables.BuildsOf(intClass.Row)[0], 70, isPermanent: true, PlayerInventories.BodyArmour, intBody));

            // 5. Offhand mode 0 (2H) refuses offhand; mode 3 accepts Shield and refuses 1H melee in Offhand1.
            var shield = Make("Metadata/Items/Armours/Shields/ShieldStr1", ItemRarity.Rare, reqLevel: 10);
            var oneHandSword = Make("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1", ItemRarity.Rare, reqLevel: 10);
            Assert.NotNull(MercenaryEquipment.CanEquip(player, MercenaryTables.ClassOf(mode0Build.ClassRow)!, mode0Build, 70,
                isPermanent: true, PlayerInventories.Offhand1, shield));
            var mode3Class = MercenaryTables.ClassOf(mode3ShieldBuild.ClassRow)!;
            Assert.NotNull(MercenaryEquipment.CanEquip(player, mode3Class, mode3ShieldBuild, 70,
                isPermanent: true, PlayerInventories.Offhand1, oneHandSword));
            // Shield passes mode 3 check when armour tags match mode3Class
            var matchingShieldMeta = mode3Class.AttributeTagIds.Contains("str_armour")
                ? "Metadata/Items/Armours/Shields/ShieldStr1"
                : mode3Class.AttributeTagIds.Contains("int_armour")
                    ? "Metadata/Items/Armours/Shields/ShieldInt1"
                    : "Metadata/Items/Armours/Shields/ShieldDex1";
            Assert.Null(MercenaryEquipment.CanEquip(player, mode3Class, mode3ShieldBuild, 70,
                isPermanent: true, PlayerInventories.Offhand1, Make(matchingShieldMeta, ItemRarity.Rare, reqLevel: 10)));

            // 6. Unique slot stats: without Luminary unique nodes, ALL unique slots are blocked (including BodyArmour1).
            var uniqueRing = rareRing with { Rarity = ItemRarity.Unique };
            var uniqueBody = strBody with { Rarity = ItemRarity.Unique };
            Assert.NotNull(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: true, PlayerInventories.Ring1, uniqueRing));
            Assert.NotNull(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: true, PlayerInventories.BodyArmour, uniqueBody));

            // Allocate Legendary Rings (AscendancyLuminary20): Unique Ring allowed, Unique BodyArmour STILL blocked!
            player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminaryStart")!.GraphId);
            player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminary9_")!.GraphId);
            player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminary20")!.GraphId);
            player.Character.StatsChanged?.Invoke(StatInput.Passives);
            Assert.Null(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: true, PlayerInventories.Ring1, uniqueRing));
            Assert.NotNull(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: true, PlayerInventories.BodyArmour, uniqueBody));

            // Phase 2 GameEvents.CompanionRules.AllowAllUniqueSlots waives unique-slot restrictions (including BodyArmour1).
            using (GameEvents.CompanionRules.Add("Game", check => { if (ReferenceEquals(check.Player, player)) check.AllowAllUniqueSlots = true; }))
            {
                Assert.Null(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: true, PlayerInventories.BodyArmour, uniqueBody));
            }

            // 7. Excess Abyssal sockets check (0x142481261).
            var badAbyssBelt = Make("Metadata/Items/Belts/Belt1", ItemRarity.Rare, reqLevel: 10)
                with { Sockets = [new ItemSocket(SocketColour.Abyss, null)] };
            Assert.NotNull(MercenaryEquipment.CanEquip(player, strClass, strBuild, 70, isPermanent: true, PlayerInventories.Belt, badAbyssBelt));

            // 8. MercenaryBuildExtraStats piecewise-linear level curve (max(recLvl, min(68, areaLvl))).
            var sampleRow = new MercenaryTables.ExtraStatRow(0, "test", Stat.BaseStrength, Level24: 24, Level68: 68, Level84: 100, Category: 0);
            Assert.Equal(24, MercenaryTables.InterpolateExtraStat(sampleRow, 12));
            Assert.Equal(24, MercenaryTables.InterpolateExtraStat(sampleRow, 24));
            Assert.Equal(46, MercenaryTables.InterpolateExtraStat(sampleRow, 46));
            Assert.Equal(68, MercenaryTables.InterpolateExtraStat(sampleRow, 68));
            Assert.Equal(84, MercenaryTables.InterpolateExtraStat(sampleRow, 76));
            Assert.Equal(100, MercenaryTables.InterpolateExtraStat(sampleRow, 84));
            Assert.Equal(100, MercenaryTables.InterpolateExtraStat(sampleRow, 95));

            // 9. ComputeStats emits Defence tab stats + IsPermanentMercenary=1 for permanent vs 0 for temporary.
            var invItems = new[] { new InventoryItem(1, 0, 0, strBody), new InventoryItem(2, 9, 0, uniqueRing) };
            var permComputed = MercenaryEquipment.ComputeStats(player, strClass, strBuild, 75, 75, invItems, isPermanent: true);
            var permDict = permComputed.ClientStats.ToDictionary(x => x.Key, x => x.Value);
            Assert.Equal(1, permDict[Stat.IsPermanentMercenary]);
            Assert.Equal(1, permDict[Stat.IsHiredMercenary]);
            Assert.Equal(1, permDict[Stat.MercenaryCanUseUniqueRing]);
            Assert.Equal(0, permDict[Stat.MercenaryCanUseUniqueHelmet]);
            Assert.True(permComputed.MaxLife > 0);
            Assert.Equal(1, permComputed.EquippedUniqueCount);

            var tempComputed = MercenaryEquipment.ComputeStats(player, strClass, strBuild, 75, 75, invItems, isPermanent: false);
            var tempDict = tempComputed.ClientStats.ToDictionary(x => x.Key, x => x.Value);
            Assert.Equal(0, tempDict[Stat.IsPermanentMercenary]);
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            lifetime.Stop(_ => { });
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [GameFilesFact]
    public async Task Permanent_hire_combat_companion_death_revive_town_invite_and_dismiss_end_to_end()
    {
        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 72);
        var mods = new MapMods(new Dictionary<uint, int> { [Stat.MapMercenaryLeague] = 1 });
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 5, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x7062, 72, stats);
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("LuminaryOwner", new(layout.Hash, area.Id, "Strand", 5) { Level = 72 }, mods));

        TestEnvironment.OpenOwnSaves("merc-e2e-hire");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameLogic(lifetime));
        var module = new ModuleLifetime("Mercenary");
        new MercenaryModule().Load(Context(module));
        await using var wire = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        var player = wire.Player;
        player.LeagueModule = new(new League("Standard", "Standard", false, false), new HashSet<string>(["Game", "Mercenary"]));
        player.Character.ClassRow = 2; // Scion
        player.Character.Experience = CharacterStats.LevelStarts[80];
        player.Pools = new(80, 2500, 1000, 500, 0, Strength: 200, Dexterity: 200, Intelligence: 200);
        player.PlayerLife = 2500;
        player.PlayerMana = 1000;
        // Allocate Noble Blood (perm follower) + Bestowed Knighthood (taunt + aura effect) + Loyal Bodyguard
        player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminaryStart")!.GraphId);
        player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminary5")!.GraphId);
        player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminary9_")!.GraphId);
        player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminary3")!.GraphId);
        player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminary12")!.GraphId);
        player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminary1")!.GraphId);
        player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminary13")!.GraphId);
        player.Character.StatsChanged?.Invoke(StatInput.Passives);
        Game.PersonalStashOf(player).AddGold(100_000);
        instance.World.Runner = player;

        try
        {
            await GameEvents.RunAreaPopulated(new AreaPopulated(player, 72, (int)seed, []));
            await GameEvents.RunPlayerEntered(player);

            // Verify IsMapOwner quest flag is granted to the encounter owner.
            Assert.True(GameEvents.HasAreaQuestFlag(player, "IsMapOwner"));

            // Open encounter panel (0x61), then send Hire Join Immediately ([2, 1]) on ruleset 0xa1.
            var placed = System.Text.Json.JsonSerializer.Deserialize<MercenaryModule.Run>(instance.ContentState["Mercenary.Encounter"])!;
            Assert.Equal(MercenaryModule.Stage.Met, placed.Stage);
            Assert.NotEmpty(placed.GearItems);
            Assert.True(await GameEvents.AnyNpcTalk(player, placed.Body, player.Npcs[placed.Body]));

            byte encRow = MercenaryTables.EncounterRulesRow!.Value;
            byte areaRow = MercenaryTables.AreaRulesRow!.Value;
            Assert.True(await GameEvents.AnyPacket(player, InstanceRulesets.ActionId, [0x02, 0xca, encRow, 0x02, 0x02, 0x01]));
            player.Clock.Start();
            await GameEvents.RunAreaTick(GameEvents.AreaTickWork(player), player, 3.0);

            // Win the duel -> permanent mercenary hired into key 0, Mercenary1 populated, companion spawned!
            var duel = System.Text.Json.JsonSerializer.Deserialize<MercenaryModule.Run>(instance.ContentState["Mercenary.Encounter"])!;
            Assert.Equal(MercenaryModule.Stage.Duel, duel.Stage);
            var opponent = player.Area.Monsters[duel.Body];
            opponent.Dead = true;
            await GameEvents.RunKill(new KillContext(player, opponent, opponent.Variety!.Id, (duel.X, duel.Y)));

            var roster = MercenaryModule.LoadRoster(player);
            Assert.True(roster.Slots.ContainsKey(0));
            Assert.Equal((byte)0, roster.InvitedKey);
            Assert.False(roster.Slots[0].Dead);
            var hiredGear = Game.ItemsOf(player).Items(PlayerInventories.Mercenary1);
            Assert.NotEmpty(hiredGear);
            Assert.All(hiredGear, gear =>
            {
                Assert.True(gear.Item.MercenaryLocked);
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(gear.Item.Payload().AsSpan(4, 2));
                Assert.True((flags & Item.MercenaryLockedHeaderFlag) != 0);
            });
            Assert.NotNull(player.Companion);
            Assert.True(player.Companion!.IsPermanent);

            // Clicking the hired companion opens MercenaryLoadout (0x7b 0x60).
            uint compId = player.Companion.EntityId;
            Assert.True(player.Npcs.ContainsKey(compId));
            Assert.True(await GameEvents.AnyNpcTalk(player, compId, player.Npcs[compId]));

            // C->S 0x2ca ruleset 0xa2 sub 4 (ShowCurrentMercenary) & sub 5 (ShowTemporaryMercenary) decode and emit 0x7b 0x60 cleanly.
            Assert.True(await GameEvents.AnyPacket(player, InstanceRulesets.ActionId, [0x02, 0xca, areaRow, 0x01, 0x04]));
            Assert.True(await GameEvents.AnyPacket(player, InstanceRulesets.ActionId, [0x02, 0xca, areaRow, 0x01, 0x05]));

            // Companion takes lethal damage -> dies, marked Dead in persisted roster, Player.Companion cleared!
            int lethal = (int)player.Companion.Monster.MaxLife + 100_000;
            Assert.NotNull(player.Companion.ApplyDamageAsync);
            await player.Companion.ApplyDamageAsync!(player, lethal, 5.0);
            Assert.Null(player.Companion);
            roster = MercenaryModule.LoadRoster(player);
            Assert.True(roster.Slots[0].Dead);

            // Revive via C->S 0x2ca ruleset 0xa2 [2, 0] -> pays gold, clears Dead!
            uint goldBeforeRevive = Game.PersonalStashOf(player).Gold;
            Assert.True(await GameEvents.AnyPacket(player, InstanceRulesets.ActionId, [0x02, 0xca, areaRow, 0x02, 0x02, 0x00]));
            roster = MercenaryModule.LoadRoster(player);
            Assert.False(roster.Slots[0].Dead);
            Assert.True(Game.PersonalStashOf(player).Gold < goldBeforeRevive);

            // Reserve via C->S 0x2ca ruleset 0xa2 [1] -> InvitedKey becomes null.
            Assert.True(await GameEvents.AnyPacket(player, InstanceRulesets.ActionId, [0x02, 0xca, areaRow, 0x01, 0x01]));
            Assert.Null(MercenaryModule.LoadRoster(player).InvitedKey);

            // Dismiss via C->S 0x2ca ruleset 0xa2 [3, 0] -> removes from roster and clears Mercenary1 inventory!
            Assert.True(await GameEvents.AnyPacket(player, InstanceRulesets.ActionId, [0x02, 0xca, areaRow, 0x02, 0x03, 0x00]));
            Assert.Empty(MercenaryModule.LoadRoster(player).Slots);
            Assert.Empty(Game.ItemsOf(player).Items(PlayerInventories.Mercenary1));
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            module.Stop(ex => throw ex);
            GameEventOwners.Release("Mercenary", GameEventOwners.ModuleRank);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    [Fact]
    public async Task ShowCurrentMercenary_0xa2_subs_4_and_5_emit_0x7b_0x60_and_MercenaryWire_has_no_dead_encoders()
    {
        // Dead encoders removed per review must not exist on MercenaryWire.
        Assert.Null(typeof(MercenaryWire).GetField("AreaRulesBody1"));
        Assert.Null(typeof(MercenaryWire).GetField("AreaRulesBody2"));
        Assert.Null(typeof(MercenaryWire).GetMethod("EncounterRulesBody"));

        MercenaryModule.LoadData();
        TestEnvironment.OpenOwnSaves("merc-show-current");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameLogic(lifetime));
        var module = new ModuleLifetime("Mercenary");
        new MercenaryModule().Load(Context(module));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accepting = listener.AcceptTcpClientAsync();
        using var writer = new TcpClient();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        using var captured = new MemoryStream();
        var drain = reader.GetStream().CopyToAsync(captured);

        var instance = new InstanceState();
        var layout = new AreaLayout(1, "1_3_town", 200, 200, 50, 50, null, []);
        var cipher = new ConnectionCiphers(new byte[64], false);
        var player = new Player(layout, 1, 1)
        {
            Connection = { LockstepClient = false, PlayerSent = true, Session = new("merc-show", writer.GetStream(), cipher) },
            Character = { Name = "ShowMerc" + Guid.NewGuid().ToString("N")[..8], ClassRow = 2, League = "Standard", Experience = CharacterStats.LevelStarts[80] },
            Instance = instance,
            PlayerLife = 2000,
            PlayerMana = 1000,
            Pools = new(80, 2000, 1000, 500, 0, Strength: 200, Dexterity: 200, Intelligence: 200),
            LeagueModule = new(new League("Standard", "Standard", false, false), new HashSet<string>(["Game", "Mercenary"])),
        };
        player.Clock.Stop();
        player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminaryStart")!.GraphId);
        player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminary5")!.GraphId);
        player.Character.Passives.Add(Program.Passives.FindById("AscendancyLuminary9_")!.GraphId);
        player.Character.StatsChanged?.Invoke(StatInput.Passives);

        try
        {
            // Seed a roster with permanent slot 0 (invited) and temporary slot 3.
            var cls = MercenaryTables.Classes.First(c => MercenaryTables.BuildsOf(c.Row).Count > 0);
            var bld = MercenaryTables.BuildsOf(cls.Row)[0];
            var seedJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                InvitedKey = (byte?)0,
                Slots = new Dictionary<byte, object>
                {
                    [0] = new { Key = (byte)0, IsPermanent = true, ClassRow = cls.Row, BuildRow = bld.Row, Level = (byte)75, FirstNameHash32 = 1u, SecondNameHash32 = 2u, FlavourHash16 = (ushort)3, Skills = Array.Empty<object>(), Dead = false, ReviveCost = 0u, DisplayName = "Perm" },
                    [3] = new { Key = (byte)3, IsPermanent = false, ClassRow = cls.Row, BuildRow = bld.Row, Level = (byte)75, FirstNameHash32 = 4u, SecondNameHash32 = 5u, FlavourHash16 = (ushort)6, Skills = Array.Empty<object>(), Dead = false, ReviveCost = 0u, DisplayName = "Temp" },
                },
                ExiledPairs = Array.Empty<byte[]>(),
            });
            Assert.True(CharacterSaves.ChangeModuleData(player.Character.Name!, "mercenary.roster", _ => seedJson));

            byte areaRow = MercenaryTables.AreaRulesRow!.Value;
            Assert.True(await GameEvents.AnyPacket(player, InstanceRulesets.ActionId, [0x02, 0xca, areaRow, 0x01, 0x04]));
            Assert.True(await GameEvents.AnyPacket(player, InstanceRulesets.ActionId, [0x02, 0xca, areaRow, 0x01, 0x05]));

            writer.Dispose();
            await drain;

            byte[] expectedCurrent = InstanceWaypoints.OpenUi(InstanceWaypoints.MercenaryLoadoutUi, 0u, 0u);
            byte[] expectedTemp = InstanceWaypoints.OpenUi(InstanceWaypoints.MercenaryLoadoutUi, 0u, 3u);
            byte[] wireBytes = captured.ToArray();
            var decrypt = new ConnectionCiphers(new byte[64], isClient: true);
            decrypt.Receive.Process(wireBytes);

            Assert.True(wireBytes.AsSpan().IndexOf(expectedCurrent) >= 0, "Expected S->C 0x7b 0x60 for invited permanent key 0");
            Assert.True(wireBytes.AsSpan().IndexOf(expectedTemp) >= 0, "Expected S->C 0x7b 0x60 for temporary key 3");
        }
        finally
        {
            player.Closed = true;
            PlayerInventories.Forget(player.Character.Name!);
            module.Stop(ex => throw ex);
            GameEventOwners.Release("Mercenary", GameEventOwners.ModuleRank);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }
}
