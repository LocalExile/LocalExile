using GameServer.Core;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class DiscordBugReportFixesTests : IDisposable
{
    public void Dispose() => TestEnvironment.ReleaseOwnSaves();

    [Fact]
    public void Mirage_archer_stash_quest_items_catalysts_and_cluster_jewels_enforce_rules()
    {
        var items = Program.Items;
        var monsters = new MonsterCatalog();
        var rolls = new ItemRolls(items);

        // 1. Non-blocking monsters (Mirage Archer) clear Positioned.blocking and set flags1 0x08 (no walker block).
        var mirage = monsters.Find("Metadata/Monsters/Mirage/RangerMirage");
        Assert.NotNull(mirage);
        Assert.False(mirage!.PositionedBlocking);
        byte[] nonBlocking = (new MonsterObject(10, 20, 100, MonsterObject.BaseComponents, null) { Blocking = false }).Blob();
        byte[] blocking = (new MonsterObject(10, 20, 100, MonsterObject.BaseComponents, null) { Blocking = true }).Blob();
        // Blob header: objectFlags (1), baseDataCount (1), Positioned: x (4), y (4), rot (4), team (2), flags0 (1), flags1 (1)...
        Assert.Equal(0x0a, nonBlocking[17]);
        Assert.Equal(0x00, nonBlocking[18]); // hidden = 0
        Assert.Equal(0x00, nonBlocking[19]); // blocking = 0
        Assert.Equal(0x01, nonBlocking[20]); // noWalkerBlock = 1
        // Default blocking is left to the object type's own .ot Positioned flag (flags1 = 0, no 3-byte override).
        Assert.Equal(0x00, blocking[17]);
        Assert.Equal(nonBlocking.Length - 3, blocking.Length);

        // 2. Quest items (Sewer Keys) are refused by personal/guild stash grid tabs, crafting slots, and AddStacked.
        TestEnvironment.OpenOwnSaves("discordfixes");
        string account = "DiscordFixes" + Guid.NewGuid().ToString("N");
        var stash = AccountStash.For(account, items);
        var previousLogic = Program.GameLogicOrNull;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            PlayerInventories.Shape? Shape(Item i) => items.ByHash(i.TypeHash) is { } t
                ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
            var sewerKeys = ItemCatalog.Create(items.Find("Metadata/Items/QuestItems/SewerKeys")!);
            Assert.Null(stash.Place(0, 0, 0, new InventoryItem(1, 0, 0, sewerKeys), Shape, out string whyPlace));
            Assert.Contains("quest", whyPlace, StringComparison.OrdinalIgnoreCase);
            Assert.Null(stash.AddStacked(0, sewerKeys, Shape, out string whyStack));
            Assert.Contains("quest", whyStack, StringComparison.OrdinalIgnoreCase);
            int currencyTab = Enumerable.Range(0, stash.Count).Single(i => stash.TabType(i) == StashRules.Currency);
            Assert.Null(stash.Place(currencyTab, 28, 0, new InventoryItem(2, 0, 0, sewerKeys), Shape, out string whyCraft));
            Assert.Contains("quest", whyCraft, StringComparison.OrdinalIgnoreCase);

            // 3. Catalysts and Tainted Catalyst apply to rings/amulets/belts and Sinistral/Dextral scale prefix/suffix stats.
            var ringType = items.Find("Metadata/Items/Rings/Ring1")!;
            var ring = ItemCatalog.Create(ringType);
            var turbulent = items.Find("Metadata/Items/Currency/CurrencyJewelleryQualityElemental")!;
            var sinistral = items.Find("Metadata/Items/Currency/CurrencyJewelleryQualityPrefix")!;
            Assert.True(CurrencyEffects.TryAction(items, rolls, ring, turbulent, new Random(1), out var q5));
            Assert.NotNull(q5);
            Assert.Equal((byte)5, q5!.Quality);
            Assert.Equal(items.AlternateQualityOf(turbulent), q5.AlternateQualityType);
            // Switching catalyst replaces the old quality type and starts from the new gain.
            Assert.True(CurrencyEffects.TryAction(items, rolls, q5 with { Quality = 20 }, sinistral, new Random(1), out var switched));
            Assert.NotNull(switched);
            Assert.Equal((byte)5, switched!.Quality);
            Assert.Equal(items.AlternateQualityOf(sinistral), switched.AlternateQualityType);
            // Corrupted jewellery refuses normal catalysts and accepts Tainted Catalyst.
            var corruptedRing = ring with { Corrupted = true };
            Assert.True(CurrencyEffects.TryAction(items, rolls, corruptedRing, turbulent, new Random(1), out var refused));
            Assert.Null(refused);
            var taintedResult = CurrencyEffects.Tainted(CurrencyEffects.TaintedCatalyst, corruptedRing, ringType, rolls, new Random(7));
            Assert.NotNull(taintedResult);
            Assert.True(taintedResult!.Corrupted && taintedResult.Quality is >= 1 and <= 20 && taintedResult.AlternateQualityType is not null);
            // Non-jewellery (armour) is refused.
            var armour = ItemCatalog.Create(items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!);
            Assert.True(CurrencyEffects.TryAction(items, rolls, armour, turbulent, new Random(1), out var nonJewellery));
            Assert.Null(nonJewellery);

            // 4. Cluster jewels: block bases match 39 * ClusterIndex, unidentified/zero-budget jewels refused,
            //    and Medium cluster jewel inside a Large tree socket opens its child socket while Megalomaniac opens none.
            foreach (int cell in new[] { 11, 12, 13, 15, 17, 20 })
                Assert.Equal(39 * Program.Passives.ClusterIndex(cell), Program.Passives.ClusterBlockBase(cell));
            var player = new Player(new(1, "clusterfixes", 100, 100, 100, 100, null, []), 1, 1)
            {
                Character = { Name = account, ClassRow = 1 },
                Pools = CharacterStats.For(Program.Skills.ClassFor(1), 100),
                LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            player.Character.Passives.Add(Program.Passives.JewelNodeForSlot(20)!.Value);
            var mediumType = items.Find("Medium Cluster Jewel")!;
            var unidMedium = ItemCatalog.Create(mediumType) with { Identified = false };
            Assert.NotNull(Game.PassiveJewelRefusal(player, 20, 0, unidMedium));
            var emptyCluster = ItemCatalog.Create(mediumType) with { Mods = [] };
            Assert.NotNull(Game.PassiveJewelRefusal(player, 20, 0, emptyCluster));
            var validMedium = ClusterJewels.Create(mediumType, ClusterJewels.FindTheme(mediumType.Cluster!, "Fire Damage"), 5, 1)!;
            Assert.Null(Game.PassiveJewelRefusal(player, 20, 0, validMedium));
            Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, 20, 0, validMedium);
            Assert.True(Game.ClusterSlotOpen(player, 51));
            var layout = ClusterJewelLayout.For(20, validMedium)!;
            Assert.True(Game.ClusterNodeExists(player, (ushort)(layout.Base + layout.EntryOffset)));
            Assert.False(Game.ClusterNodeExists(player, Game.MaxClusterNodeId));
            // Nested cluster jewel must be strictly smaller than its parent (Small fits inside Medium, another Medium does not).
            var smallCluster = ClusterJewels.Create(items.Find("Small Cluster Jewel")!, null, 3, 0)!;
            Assert.Null(Game.PassiveJewelRefusal(player, 51, 0, smallCluster));
            Assert.NotNull(Game.PassiveJewelRefusal(player, 51, 0, validMedium));
            // Megalomaniac has 0 sockets and must not open child cells.
            var mega = new UniqueCatalog(items).Create("Megalomaniac", new Random(1))!;
            Game.ItemsOf(player).Remove(PlayerInventories.PassiveJewels, Game.ItemsOf(player).Items(PlayerInventories.PassiveJewels).Single().Id);
            Game.ItemsOf(player).Add(PlayerInventories.PassiveJewels, 20, 0, mega);
            Assert.False(Game.ClusterSlotOpen(player, 51));

            // 5. Snipe & General's Cry hybrid gems never emit their support-only Effect2 into active skills.
            foreach (string gemId in new[] { "Metadata/Items/Gems/SkillGemChannelledSnipe", "Metadata/Items/Gems/SkillGemGeneralsCry" })
            {
                var gem = ItemCatalog.Create(items.Find(gemId)!);
                var effects = items.GemEffects(gem).ToArray();
                Assert.Single(effects);
            }
        }
        finally
        {
            PlayerInventories.Forget(account);
            AccountStash.Forget(account);
            Program.SetGameLogic(previousLogic!);
        }
    }

    [Fact]
    public void Act1_story_objects_boss_kits_and_kinetic_bolt_volley_match_native_rules()
    {
        var skills = Program.Skills;
        var monsters = new MonsterCatalog();

        // 1. Fairgraves summons Ghostflame Raging Spirits (not Adolescent Rhoa) and throws SpectralThrowFairgraves.
        int fairgravesSrs = skills.EffectRow("SummonRagingSpiritFairgraves");
        var srsSummon = skills.MonsterSummon(fairgravesSrs, 12);
        Assert.NotNull(srsSummon);
        Assert.Equal(monsters.Find("Metadata/Monsters/MonsterSummonedSkull/MonsterSummonedSkullGhostly")!.Row,
            srsSummon!.Variety);
        int fairgravesThrow = skills.EffectRow("FairgravesThrownWeapon");
        Assert.Equal("Metadata/Projectiles/SpectralThrowFairgraves", skills.MonsterProjectile(fairgravesThrow)?.Metadata);

        // Weapon projectiles come from monster_projectile_variation's MonsterProjectileAttack row, never from another
        // monster's effect on the shared monster_projectile_weapon skill.
        Assert.Equal("Metadata/Projectiles/LegionMarakethRangedDash", skills.VariationProjectile(1790)?.Id);
        Assert.Equal("Metadata/Projectiles/LegionLioneyeSnipeMedium", skills.VariationProjectile(10691)?.Id);
        int weaponSkill = ClientTables.Table("ActiveSkills").ToList().FindIndex(r => r.TextOrNull("Id") == "monster_projectile_weapon");
        int borrowers = 0;
        foreach (var (e, row) in ClientTables.Table("GrantedEffects").Select((r, i) => (r, i)))
            if (e.IntOrNull("ActiveSkill") == weaponSkill && row != 801 && skills.VariationProjectile(row) is null)
            {
                borrowers++;
                Assert.NotEqual("Metadata/Projectiles/AtlasBasiliskSnakeProjectile", skills.SharedProjectile(row)?.Id);
            }
        Assert.True(borrowers > 0);

        // 2. Merveil fires IceSpear and summons statues/daughters/spawn; Amarissa/Ambrosia start targetable (state 2);
        //    Brutus starts with dialogue_lines = 1.
        Assert.Equal("Metadata/Projectiles/IceSpear", skills.MonsterProjectile(skills.EffectRow("MerveilIceSpear"))?.Metadata);
        Assert.Equal(monsters.Find("Metadata/Monsters/Statue/GoldStatueFemaleSwordMerveil")!.Row,
            skills.MonsterSummon(skills.EffectRow("MerveilSummonStatue"), 14)!.Variety);
        Assert.Equal(monsters.Find("Metadata/Monsters/Seawitch/SeaWitchMerveilSummonFire")!.Row,
            skills.MonsterSummon(skills.EffectRow("MerveilSummonDaughters"), 14)!.Variety);
        Assert.Equal(monsters.Find("Metadata/Monsters/Seawitch/SeaWitchSpawnExploding")!.Row,
            skills.MonsterSummon(skills.EffectRow("MerveilSummonSpawn"), 14)!.Variety);
        Assert.Equal((byte)2, BossKits.StartState(monsters.Find("Metadata/Monsters/Seawitch/SeaWitchBossSpark")!, 1));
        Assert.Equal((byte)2, BossKits.StartState(monsters.Find("Metadata/Monsters/Seawitch/SeaWitchBossFirestorm")!, 1));
        Assert.Equal((byte)1, BossKits.StartState(monsters.Find("Metadata/Monsters/seawitchillusion/BossMerveil")!, 2));
        var brutusStates = BossKits.StartStates(monsters.Find("Metadata/Monsters/Brute/BossBrute")!);
        Assert.NotNull(brutusStates);
        int dialogueIdx = brutusStates!.Names!.ToList().IndexOf("dialogue_lines");
        Assert.True(dialogueIdx >= 0);
        Assert.Equal(1, brutusStates.SharedStates[dialogueIdx]);

        // 3. Kinetic Bolt multi-projectile fan alternates zig-zag turn parity per volley index (LMP/GMP/Fragmentation).
        var kb = GatewayProjectileSkillProfiles.Resolve("KineticBolt", "Metadata/Projectiles/ZigZagWandProjectile",
            stat => stat switch
            {
                Stat.KineticWandBaseNumberOfZigZags => 2,
                Stat.NumberOfAdditionalProjectiles => 2,
                _ => 0,
            }, 100, attack: true);
        kb = GatewayProjectileSkillProfiles.WithKineticBoltLegs(kb, sequence: 2);
        var leg0 = kb.Legs!(0);
        var leg1 = kb.Legs!(1);
        leg0.First(new(0, 0), new(50, 0));
        leg1.First(new(0, 0), new(50, 0));
        var turn0 = leg0.NextTurn(new(15, 0), blocked: false)!.Value;
        var turn1 = leg1.NextTurn(new(15, 0), blocked: false)!.Value;
        Assert.True(turn0.Leg.Target.Y * turn1.Leg.Target.Y < 0);
    }
}
