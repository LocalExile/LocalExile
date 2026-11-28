using System.Net;
using System.Net.Sockets;
using GameServer.Core.Accounts;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using GameServer.Modules.Game;
using GameServer.Modules.Mercenary;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class MercenaryCompanionCombatTests
{
    static IModuleContext Context(IModuleLifetime lifetime)
    {
        var context = System.Reflection.DispatchProxy.Create<IModuleContext, MercenaryTests.Hook>();
        ((MercenaryTests.Hook)(object)context).Lifetime = lifetime;
        return context;
    }

    static MercenaryTables.SkillRow FindSkill(string effectId)
    {
        var skills = Program.Skills;
        for (int row = 0; row < 400; row++)
        {
            if (MercenaryTables.SkillOf(row) is { } s && s.GrantedEffect >= 0
                && string.Equals(skills.EffectId(s.GrantedEffect), effectId, StringComparison.Ordinal))
                return s;
        }
        throw new InvalidOperationException($"MercenarySkills row for {effectId} not found");
    }

    static MercenaryTables.SupportRow FindSupport(string supportId)
    {
        for (int row = 0; row < 800; row++)
        {
            if (MercenaryTables.SupportOf(row) is { } s
                && string.Equals(s.Id, supportId, StringComparison.Ordinal))
                return s;
        }
        throw new InvalidOperationException($"MercenarySupports row for {supportId} not found");
    }

    static MercenaryTables.RolledSkill Rolled(string effectId, params string[] supportIds)
    {
        var skill = FindSkill(effectId);
        List<int> suppRows = [];
        List<ushort> suppHashes = [];
        foreach (string sid in supportIds)
        {
            var supp = FindSupport(sid);
            suppRows.Add(supp.Row);
            suppHashes.Add(supp.Hash16);
        }
        return new MercenaryTables.RolledSkill(skill.Row, skill.Hash16, suppRows, suppHashes);
    }

    static Item MakeItem(string metadataPath, byte quality = 0, params (string ModId, int[] Values)[] explicitMods)
    {
        var bt = Program.Items.Find(metadataPath) ?? throw new InvalidOperationException($"Missing base type {metadataPath}");
        var item = ItemCatalog.Create(bt) with { Rarity = ItemRarity.Rare, Identified = true, Quality = quality };
        if (explicitMods.Length == 0) return item;
        var mods = new List<ItemMod>(item.Mods);
        foreach (var (modId, values) in explicitMods)
        {
            var mod = Program.Items.Mods.ById(modId) ?? throw new InvalidOperationException($"Missing mod {modId}");
            mods.Add(new ItemMod(ModSection.Explicit, mod.Hash, values));
        }
        return item with { Mods = mods };
    }

    static SpawnedMonster SpawnDummyEnemy(Player player, uint id, int x, int y, uint maxLife = 500_000)
    {
        var variety = Game.Monsters.Find("Metadata/Monsters/Zombie/ZombieStandard")
            ?? Game.Monsters.All.First(v => v.Supported);
        var enemy = new SpawnedMonster("DummyTarget", new PlayerWalk(x, y, 10), aggroRange: 60, stopRange: 8, maxLife: maxLife)
        {
            ObjectId = id,
            Level = 75,
            Life = maxLife,
            MaxLife = maxLife,
            Hostile = true,
            Variety = variety,
            Mods = MonsterModSet.None,
        };
        player.Area.Monsters[id] = enemy;
        player.Area.Zones.Grid.Set(new(id), (x, y));
        return enemy;
    }

    static void AllocateLuminaryFollower(Player player)
    {
        var tree = Game.PassivesFor(player);
        player.Character.Passives.Add(tree.FindById("AscendancyLuminaryStart")!.GraphId);
        player.Character.Passives.Add(tree.FindById("AscendancyLuminary5")!.GraphId);
        player.Character.Passives.Add(tree.FindById("AscendancyLuminary9_")!.GraphId);
        player.Character.Passives.Add(tree.FindById("AscendancyLuminary4")!.GraphId); // +15% merc dmg
        player.Character.StatsChanged?.Invoke(StatInput.Passives);
    }

    static async Task StepCompanionTicks(Player player, double fromNow, double toNow, double step = 0.1)
    {
        var session = Game.ProjectilesFor(player);
        for (double t = fromNow; t <= toNow + 1e-6; t += step)
        {
            foreach (var tick in GameEvents.CompanionTick.For(player))
                await tick(player, t);
            using (Game.ProjectileMutation(player))
                session.Runtime.AdvanceTo(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(t)));
        }
    }

    [Fact]
    public async Task Companion_spawns_with_ownedKit_Actor_skills_refreshes_on_gear_change_and_scales_weapon_gear_build_damage_and_speed()
    {
        MercenaryModule.LoadData();
        TestEnvironment.OpenOwnSaves("merc-comp-scaling");
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
        var layout = new AreaLayout(1, "1_1_2", 300, 300, 100, 100, null, []);
        var cipher = new ConnectionCiphers(new byte[64], false);
        var player = new Player(layout, 1, 1)
        {
            Connection = { LockstepClient = false, PlayerSent = true, Session = new("merc-scale", writer.GetStream(), cipher) },
            Character = { Name = "MercScale" + Guid.NewGuid().ToString("N")[..8], ClassRow = 2, League = "Standard", Experience = CharacterStats.LevelStarts[85] },
            Instance = instance,
            PlayerLife = 3000,
            PlayerMana = 1000,
            Pools = new(85, 3000, 1000, 500, 0, Strength: 200, Dexterity: 200, Intelligence: 200),
            LeagueModule = new(new League("Standard", "Standard", false, false), new HashSet<string>(["Game", "Mercenary"])),
        };
        player.Clock.Start();
        player.Walk.Teleport(100, 100, 0);
        AllocateLuminaryFollower(player);

        try
        {
            // 1. Pick a melee/2H Str mercenary build and give it Ground Slam + Melee Physical Damage + Attack Speed supports.
            var strClass = MercenaryTables.Classes.First(c => c.Attribute == "Str" && MercenaryTables.BuildsOf(c.Row).Count > 0);
            var strBuild = MercenaryTables.BuildsOf(strClass.Row)[0];
            var groundSlam = Rolled("GroundSlamMercenary", "MeleePhysHigh", "AttackSpeedHigh");
            var seedRoster = new MercenaryModule.RosterState
            {
                InvitedKey = 0,
                Slots = new Dictionary<byte, MercenaryModule.HiredMercenary>
                {
                    [0] = new MercenaryModule.HiredMercenary
                    {
                        Key = 0,
                        ClassRow = strClass.Row,
                        BuildRow = strBuild.Row,
                        Level = 80,
                        FirstNameHash32 = 1u,
                        SecondNameHash32 = 2u,
                        FlavourHash16 = 3,
                        Skills = [groundSlam],
                        Dead = false,
                    },
                },
            };
            string rosterJson = System.Text.Json.JsonSerializer.Serialize(seedRoster);
            Assert.True(CharacterSaves.ChangeModuleData(player.Character.Name!, "mercenary.roster", _ => rosterJson));

            // Start with a low-tier level 1 starter weapon in Mercenary1 Weapon1 slot (cell X=2).
            var inv = Game.ItemsOf(player);
            var weaponSlot = PlayerInventories.MercenarySlotForEquipmentRow(PlayerInventories.Weapon1)!.Value;
            var ringSlot = PlayerInventories.MercenarySlotForEquipmentRow(PlayerInventories.Ring1)!.Value;
            var weakSword = MakeItem("Metadata/Items/Weapons/TwoHandWeapons/TwoHandSwords/TwoHandSword1");
            inv.Add(PlayerInventories.Mercenary1, weaponSlot.CellX, 0, weakSword);

            // Enter combat area -> companion spawns with ownedSkills on Actor!
            await GameEvents.RunPlayerEntered(player);
            Assert.NotNull(player.Companion);
            var compMonster = player.Companion!.Monster;

            // (a) Verify 0x4000+ skill IDs exist on the spawned Actor (NOT variety default EASMercenaryPortalOut!).
            int groundSlamEffect = FindSkill("GroundSlamMercenary").GrantedEffect;
            Assert.Contains(compMonster.SkillIds, s => s.Id >= 0x4000 && s.Effect == groundSlamEffect);
            Assert.DoesNotContain(compMonster.SkillIds, s => Game.SkillsFor(player).EffectId(s.Effect) == BossKits.MercenaryPortalOut);
            // Verify computed ExtraStats were preserved on monster.Mods (not overwritten by rolled unique monster mods).
            Assert.True(compMonster.Mods.Effects[Stat.DamagePlusPct] > 0, "Expected MercenaryBuildExtraStats damage_+% on spawned companion");

            // Measure baseline hit damage & swing speed with weak starter weapon.
            var compAt = compMonster.Walk.Position(0);
            var dummy1 = SpawnDummyEnemy(player, 0x90000001u, compAt.X + 4, compAt.Y);
            await StepCompanionTicks(player, 0.1, 0.2, 0.1);
            double weakSwingDuration = compMonster.BusyUntil - 0.1;
            Assert.True(weakSwingDuration > 0.1, "Companion should start Ground Slam action");
            await StepCompanionTicks(player, 0.3, 2.0, 0.1);
            uint weakDamageDealt = dummy1.MaxLife - dummy1.Life;
            Assert.True(weakDamageDealt > 0, "Weak weapon companion should deal damage");

            // (b) Swap in a high-DPS endgame 2H weapon (Exquisite Blade / Vaal Greatsword with quality 20%,
            // local %phys, flat added phys, local attack speed) + ring with flat phys & global damage.
            var strongWeapon = MakeItem("Metadata/Items/Weapons/TwoHandWeapons/TwoHandSwords/TwoHandSword19", quality: 20,
                ("LocalIncreasedPhysicalDamagePercent8", [175]),
                ("LocalAddedPhysicalDamage9", [35, 65]),
                ("LocalIncreasedAttackSpeed7", [27]));
            var dmgRing = MakeItem("Metadata/Items/Rings/Ring1", quality: 0,
                ("AddedPhysicalDamage5", [10, 18]),
                ("WeaponElementalDamage5", [35]));

            var weakComputed = MercenaryEquipment.ComputeStats(player, strClass, strBuild, 80, 80,
                inv.Items(PlayerInventories.Mercenary1), isPermanent: true, [groundSlam]);
            inv.ClearRow(PlayerInventories.Mercenary1);
            inv.Add(PlayerInventories.Mercenary1, weaponSlot.CellX, 0, strongWeapon);
            inv.Add(PlayerInventories.Mercenary1, ringSlot.CellX, 0, dmgRing);
            var strongComputed = MercenaryEquipment.ComputeStats(player, strClass, strBuild, 80, 80,
                inv.Items(PlayerInventories.Mercenary1), isPermanent: true, [groundSlam]);

            Assert.NotNull(weakComputed.Weapons.MainHand);
            Assert.NotNull(strongComputed.Weapons.MainHand);
            Assert.True(strongComputed.Weapons.MainHand!.LocalDamage.MaxTotal > weakComputed.Weapons.MainHand!.LocalDamage.MaxTotal * 5,
                $"Expected high-DPS weapon local damage ({strongComputed.Weapons.MainHand.LocalDamage.MaxTotal}) >> weak weapon ({weakComputed.Weapons.MainHand.LocalDamage.MaxTotal})");
            Assert.True(strongComputed.Weapons.MainHand.AttackMs < weakComputed.Weapons.MainHand.AttackMs,
                $"Expected local attack speed to shorten AttackMs ({strongComputed.Weapons.MainHand.AttackMs} < {weakComputed.Weapons.MainHand.AttackMs})");

            // Trigger live gear refresh -> updates companion stats AND pushes Actor 0x2bc PlayerSkillsChange on the wire!
            await GameEvents.RunInventoryChanged(player, PlayerInventories.Mercenary1);

            // Reset enemy and measure upgraded hit damage & faster attack duration.
            player.Area.Monsters.Remove(dummy1.ObjectId);
            var dummy2 = SpawnDummyEnemy(player, 0x90000002u, compAt.X + 4, compAt.Y);
            double secondStart = Math.Max(3.0, compMonster.BusyUntil + 0.2);
            await StepCompanionTicks(player, secondStart, secondStart + 0.05, 0.1);
            double strongSwingDuration = compMonster.BusyUntil - secondStart;
            Assert.True(strongSwingDuration < weakSwingDuration,
                $"Expected faster weapon swing ({strongSwingDuration:0.000}s < {weakSwingDuration:0.000}s)");

            await StepCompanionTicks(player, secondStart + 0.1, secondStart + 1.5, 0.1);
            uint strongDamageDealt = dummy2.MaxLife - dummy2.Life;
            Assert.True(strongDamageDealt > weakDamageDealt * 4,
                $"Expected high-DPS gear damage ({strongDamageDealt}) >> starter weapon damage ({weakDamageDealt})");

            // Verify spell damage + cast speed scaling on a caster build too.
            var intClass = MercenaryTables.Classes.First(c => c.Attribute == "Int" && MercenaryTables.BuildsOf(c.Row).Count > 0);
            var intBuild = MercenaryTables.BuildsOf(intClass.Row)[0];
            var spark = Rolled("SparkMercenary", "CastSpeedHigh");
            var casterNoGear = MercenaryEquipment.ComputeStats(player, intClass, intBuild, 80, 80, [], isPermanent: true, [spark]);
            var spellWand = MakeItem("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1", quality: 0,
                ("SpellDamageOnWeapon5", [55]),
                ("SpellAddedLightningDamage5", [25, 80]),
                ("IncreasedCastSpeed4", [18]));
            var casterWithWand = MercenaryEquipment.ComputeStats(player, intClass, intBuild, 80, 80,
                [new InventoryItem(99, weaponSlot.CellX, 0, spellWand)], isPermanent: true, [spark]);
            Assert.True(casterWithWand.StatValues.GetValueOrDefault(Stat.SpellDamagePlusPct) > casterNoGear.StatValues.GetValueOrDefault(Stat.SpellDamagePlusPct));
            Assert.True(casterWithWand.StatValues.GetValueOrDefault(Stat.BaseCastSpeedPlusPct) > casterNoGear.StatValues.GetValueOrDefault(Stat.BaseCastSpeedPlusPct));
            Assert.True(casterWithWand.StatValues.GetValueOrDefault(Stat.SpellMaximumAddedLightningDamage) > 0);

            // Verify Actor skills change packet (0x02bc targeting ActorComponentIndex 9, flags 0x20) was sent on gear refresh.
            writer.Dispose();
            await drain;
            byte[] wireBytes = captured.ToArray();
            var decrypt = new ConnectionCiphers(new byte[64], isClient: true);
            decrypt.Receive.Process(wireBytes);
            byte[] compUpdatePrefix = new LoginWriter().U16(InstanceEntities.ComponentUpdateId)
                .U32(compMonster.ObjectId).U32(0).U16(0).ToArray();
            bool sawActorSkillsChange = false;
            int searchAt = 0;
            while (searchAt < wireBytes.Length)
            {
                int idx = wireBytes.AsSpan(searchAt).IndexOf(compUpdatePrefix);
                if (idx < 0) break;
                int pos = searchAt + idx + compUpdatePrefix.Length;
                // ComponentUpdate writes a u16 big-endian blob length followed by [componentIndex, flags0, ...]
                if (pos + 4 <= wireBytes.Length && wireBytes[pos + 2] == PlayerObject.ActorComponentIndex && wireBytes[pos + 3] == 0x20)
                {
                    sawActorSkillsChange = true;
                    break;
                }
                searchAt = pos;
            }
            Assert.True(sawActorSkillsChange,
                "Expected 0x02bc Actor (index 9, flags 0x20) PlayerSkillsChange update on companion gear change");
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

    [Fact]
    public async Task Companion_executes_projectiles_AoE_curses_self_buffs_and_rotates_skills_without_melee_fallback()
    {
        MercenaryModule.LoadData();
        TestEnvironment.OpenOwnSaves("merc-comp-skills");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameLogic(lifetime));
        var module = new ModuleLifetime("Mercenary");
        new MercenaryModule().Load(Context(module));

        var instance = new InstanceState();
        var layout = new AreaLayout(1, "1_1_2", 300, 300, 100, 100, null, []);
        await using var wire = await ScarabEncounterTests.Wire.Open(instance, layout, 1);
        var player = wire.Player;
        player.Character.Name = "MercAI" + Guid.NewGuid().ToString("N")[..8];
        player.Character.ClassRow = 2;
        player.Character.Experience = CharacterStats.LevelStarts[85];
        player.PlayerLife = 3000;
        player.PlayerMana = 1000;
        player.Pools = new(85, 3000, 1000, 500, 0, Strength: 200, Dexterity: 200, Intelligence: 200);
        player.LeagueModule = new(new League("Standard", "Standard", false, false), new HashSet<string>(["Game", "Mercenary"]));
        player.Clock.Start();
        player.Walk.Teleport(100, 100, 0);
        AllocateLuminaryFollower(player);

        try
        {
            var intClass = MercenaryTables.Classes.First(c => c.Attribute == "Int" && MercenaryTables.BuildsOf(c.Row).Count > 0);
            var intBuild = MercenaryTables.BuildsOf(intClass.Row)[0];

            // Give the caster mercenary: Molten Shell (self-buff/guard), Conductivity (hex), Frostbolt (projectile + GMP),
            // and Ice Nova (AoE spell) — two 0-cd offensive skills plus buff + curse!
            var moltenShell = Rolled("MoltenShellMercenary");
            var conductivity = Rolled("ConductivityMercenary");
            var frostbolt = Rolled("FrostboltMercenary", "MultipleProjectilesHigh", "ColdPenetrationHigh");
            var iceNova = Rolled("IceNovaMercenary", "ConcentratedEffectHigh");

            var seedRoster = new MercenaryModule.RosterState
            {
                InvitedKey = 0,
                Slots = new Dictionary<byte, MercenaryModule.HiredMercenary>
                {
                    [0] = new MercenaryModule.HiredMercenary
                    {
                        Key = 0,
                        ClassRow = intClass.Row,
                        BuildRow = intBuild.Row,
                        Level = 80,
                        FirstNameHash32 = 1u,
                        SecondNameHash32 = 2u,
                        FlavourHash16 = 3,
                        Skills = [moltenShell, conductivity, frostbolt, iceNova],
                        Dead = false,
                    },
                },
            };
            string rosterJson = System.Text.Json.JsonSerializer.Serialize(seedRoster);
            Assert.True(CharacterSaves.ChangeModuleData(player.Character.Name!, "mercenary.roster", _ => rosterJson));

            await GameEvents.RunPlayerEntered(player);
            Assert.NotNull(player.Companion);
            var comp = player.Companion!;
            var compMonster = comp.Monster;

            // Verify ALL 4 build skills are registered on Actor (and MeleeFallbackEffect was NOT forced at slot 0!).
            int msEffect = FindSkill("MoltenShellMercenary").GrantedEffect;
            int condEffect = FindSkill("ConductivityMercenary").GrantedEffect;
            int fbEffect = FindSkill("FrostboltMercenary").GrantedEffect;
            int novaEffect = FindSkill("IceNovaMercenary").GrantedEffect;
            Assert.Contains(compMonster.SkillIds, s => s.Effect == msEffect && s.Id >= 0x4000);
            Assert.Contains(compMonster.SkillIds, s => s.Effect == condEffect && s.Id >= 0x4000);
            Assert.Contains(compMonster.SkillIds, s => s.Effect == fbEffect && s.Id >= 0x4000);
            Assert.Contains(compMonster.SkillIds, s => s.Effect == novaEffect && s.Id >= 0x4000);
            Assert.DoesNotContain(compMonster.SkillIds, s => s.Effect == MercenaryTables.MeleeFallbackEffect);

            // Spawn two clustered enemies at ranged distance (20 cells away) so caster stays at range,
            // casts Molten Shell -> casts Conductivity -> fires Frostbolt projectiles -> casts AoE!
            var startPos = compMonster.Walk.Position(0);
            var enemyA = SpawnDummyEnemy(player, 0x90000101u, startPos.X + 20, startPos.Y, maxLife: 500_000);
            var enemyB = SpawnDummyEnemy(player, 0x90000102u, startPos.X + 23, startPos.Y + 2, maxLife: 500_000);

            // Step companion AI across 6 seconds of deterministic game time (no sleeps).
            var session = Game.ProjectilesFor(player);
            var usedSkillIds = new HashSet<ushort>();
            bool sawSelfBuff = false;
            bool sawCurse = false;
            for (double t = 0.1; t <= 6.0; t += 0.1)
            {
                player.Minions.OutputQueue.Clear();
                foreach (var tick in GameEvents.CompanionTick.For(player))
                    await tick(player, t);
                using (Game.ProjectileMutation(player))
                    session.Runtime.AdvanceTo(Math.Max(session.Runtime.Now, ProjectileRuntime.Time(t)));

                // Inspect ActionStarted packets emitted by the companion to verify multi-skill rotation.
                foreach (var msg in player.Minions.OutputQueue)
                {
                    if (msg.Packet.Length >= 18 && msg.Packet[0] == 0x01 && msg.Packet[1] == 0xab)
                    {
                        ushort sid = (ushort)((msg.Packet[^4] << 8) | msg.Packet[^3]);
                        if (sid >= 0x4000) usedSkillIds.Add(sid);
                    }
                }
                if (compMonster.BuffStats is { Count: > 0 }) sawSelfBuff = true;
                if (enemyA.HasCurses && enemyA.Curses.All.ContainsKey((comp.EntityId, "Conductivity"))) sawCurse = true;
            }

            // 1. Companion executed its self-buff (Molten Shell) and hex (Conductivity).
            Assert.True(sawSelfBuff, "Expected companion to cast self-buff (Molten Shell) in combat");
            Assert.True(sawCurse, "Expected companion to apply Conductivity curse onto hostile targets");
            Assert.True(enemyB.HasCurses && enemyB.Curses.All.ContainsKey((comp.EntityId, "Conductivity")),
                "Expected Conductivity AoE hex to curse clustered second enemy too");

            // 2. Companion stayed at ranged distance instead of collapsing into melee fallback.
            var endPos = compMonster.Walk.Position(6.0);
            Assert.True(Game.Dist(endPos, enemyA.Walk.Position(6.0)) >= 12,
                $"Expected ranged caster mercenary to stay at range, not walk into melee (dist={Game.Dist(endPos, enemyA.Walk.Position(6.0)):0.0})");

            // 3. Companion used multiple distinct 0x4000+ skills (buff, curse, projectile/AoE rotation).
            Assert.True(usedSkillIds.Count >= 3,
                $"Expected companion to execute >= 3 distinct build skills (got {usedSkillIds.Count}: {string.Join(", ", usedSkillIds.Select(x => $"0x{x:x4}"))})");

            // 4. Both enemies took substantial real damage (projectiles/AoE hitting multiple targets, not chip damage).
            uint dmgA = enemyA.MaxLife - enemyA.Life;
            uint dmgB = enemyB.MaxLife - enemyB.Life;
            Assert.True(dmgA >= 500, $"Expected meaningful scaled spell/projectile damage on enemyA (got {dmgA})");
            Assert.True(dmgB >= 500, $"Expected AoE/multi-projectile damage on clustered enemyB (got {dmgB})");
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
