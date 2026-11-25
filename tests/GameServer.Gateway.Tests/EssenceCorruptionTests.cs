using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class EssenceCorruptionTests
{
    [Fact]
    public void Corruption_uses_real_essence_groups_tier_caps_and_Stability_outcomes()
    {
        ChaosBypassEffects();
        LowLifeEffects();
        DecayEffects();
        ToughnessEffects();
        foreach (var (suffix, element) in new[] { ("Fire", DamageType.Fire), ("Cold", DamageType.Cold), ("Lightning", DamageType.Lightning) })
        {
            var mod = Program.Items.Mods.ById("EssenceDaemonPhysicalAddedAndConvertedTo" + suffix)!;
            Assert.Equal(MonsterModCatalog.Support.Full, MonsterModCatalog.SupportOf(mod));
            var effects = new MonsterModCatalog(Program.Items.Mods, "data").Create(
                [mod.Roll(ModSection.Implicit, new Random(505))], [], MonsterRarity.Normal, 70).Effects;
            var hit = effects.Hit(DamagePacket.Of(DamageType.Physical, 100, 200));
            Assert.Equal(new DamageRange(80, 160), hit[DamageType.Physical]);
            Assert.Equal(new DamageRange(30, 60), hit[element]);
        }
        var hatred = MapEssenceCatalog.All.Single(e => e.Type == 0 && e.Tier == 5);
        var envy = MapEssenceCatalog.All.Single(e => e.Type == 19 && e.Tier == 5);
        var source = new MapEssences.Monolith(1, 0, 100, 100, "unused", [hatred.Row, envy.Row]);
        var released = MapEssences.Corrupt(source, 70, MapMods.None, new OutcomeRoll(0))!;
        Assert.Equal(MapEssences.CorruptionOutcome.Release, released.Outcome);
        Assert.True(released.Monolith.ForcedRelease);
        Assert.True(released.Monolith.Corrupted);
        var upgrade = MapEssences.Corrupt(source, 70, MapMods.None, new OutcomeRoll(1))!;
        Assert.All(upgrade.Monolith.Essences, row => Assert.Equal(6, MapEssenceCatalog.Find(row)!.Tier));
        var added = MapEssences.Corrupt(source, 70, MapMods.None, new OutcomeRoll(2))!;
        Assert.Equal(3, added.Monolith.Essences.Count);
        Assert.DoesNotContain(MapEssenceCatalog.Find(added.Monolith.Essences[^1])!.Type, new[] { 0, 19 });
        var transformed = MapEssences.Corrupt(source, 70, MapMods.None, new OutcomeRoll(3))!;
        Assert.InRange(MapEssenceCatalog.Find(transformed.Monolith.Essences[0])!.Type, 4, 7);
        Assert.True(MapEssenceCatalog.Find(transformed.Monolith.Essences[1])!.Corrupted);
        Assert.Equal(8, MapEssenceCatalog.Find(transformed.Monolith.Essences[1])!.Tier);
        var deafening = MapEssenceCatalog.All.Single(e => e.Type == 0 && e.Tier == 7);
        Assert.Equal(deafening, MapEssenceCatalog.Higher(deafening));
        var stable = new MapMods(new Dictionary<uint, int> { [Stat.MapStableEssenceCorruption] = 1 });
        Assert.Equal(MapEssences.CorruptionOutcome.Upgrade, MapEssences.Corrupt(source, 70, stable, new OutcomeRoll(0))!.Outcome);
        Assert.Equal(MapEssences.CorruptionOutcome.Transform, MapEssences.Corrupt(source, 70, stable, new OutcomeRoll(1))!.Outcome);
        var cannotRelease = new MapMods(new Dictionary<uint, int> { [Stat.MapEssenceCorruptionCannotReleaseMonsters] = 1 });
        var prevented = MapEssences.Corrupt(source, 70, cannotRelease, new OutcomeRoll(0))!;
        Assert.Equal(MapEssences.CorruptionOutcome.Release, prevented.Outcome);
        Assert.False(prevented.Monolith.ForcedRelease);
        Assert.Equal(source.Essences, prevented.Monolith.Essences);
        var resonance = new MapMods(new Dictionary<uint, int> { [Stat.MapEssenceRemnantConvert] = 1 });
        var repeated = MapEssences.Corrupt(source, 70, resonance, new OutcomeRoll(1))!.Monolith;
        Assert.Equal(source.Essences.Count, repeated.Essences.Count);
        Assert.Single(repeated.Essences.Distinct());
        Assert.Null(MapEssences.Corrupt(repeated, 70, MapMods.None, new OutcomeRoll(0)));
        Assert.Null(MapEssences.Corrupt(source with { Stage = 4 }, 70, MapMods.None, new OutcomeRoll(0)));
        var inherited = MapEssences.Inherit(source, MapEssenceCatalog.Higher(hatred).Row)!;
        Assert.Equal(2, inherited.Essences.Count);
        Assert.Equal(6, MapEssenceCatalog.Find(inherited.Essences[0])!.Tier);
        Assert.Null(MapEssences.Inherit(inherited, hatred.Row));
        Assert.Null(MapEssences.Inherit(inherited, inherited.Essences[0]));
        var woe = MapEssenceCatalog.All.First(e => e.Type == 1);
        Assert.Equal(3, MapEssences.Inherit(inherited, woe.Row)!.Essences.Count);
        Assert.Null(MapEssences.Inherit(inherited with { Stage = 4 }, woe.Row));
    }

    [GameFilesFact]
    public async Task Vaal_payment_is_instance_owned_once_only_and_transformed_rewards_keep_identity()
    {
        TestEnvironment.OpenOwnSaves("essence-corruption");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var instance = new InstanceState();
        var world = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var mods = new MapMods(new Dictionary<uint, int> { [Stat.MapExtraMonoliths] = 3, [Stat.MapStableEssenceCorruption] = 1 });
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(world.Id, 0x505, 70, stats);
        ServerLevels.For(world.Id, seed, 70);
        var layout = AreaLayouts.For(world.Hash16, seed);
        instance.OpenedWith(new("CorruptionOwner", new(layout.Hash, world.Id, "Strand", 3) { Level = 70 }, mods));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        await using var peer = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        try
        {
            await MapEssences.Enter(owner.Player);
            await MapEssences.Enter(peer.Player);
            MapEssences.Monolith[] Read() => JsonSerializer.Deserialize<MapEssences.Monolith[]>(instance.ContentState["Essence/monoliths"])!;
            var target = Read()[0];
            var items = Game.ItemsOf(owner.Player);
            var vaal = Program.Items.Find(CurrencyEffects.VaalOrb)!;
            var stack = items.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(vaal) with { StackSize = 3 });
            byte[] Packet(uint inventory, uint item, uint targetId) => new LoginWriter().U16(0x74).U32(inventory).U32(item).U32(targetId).ToArray();
            ushort Left() => items.Items(PlayerInventories.MainInventory).Single(i => i.Id == stack.Id).Item.StackSize;
            owner.Player.Walk.Teleport(target.X + 8, target.Y, 0);
            Assert.False(await MapEssences.UseItemOn(owner.Player, Packet(1, stack.Id, uint.MaxValue)));
            Assert.True(await MapEssences.UseItemOn(owner.Player, Packet(2, stack.Id, target.Id)));
            Assert.Equal(3, Left());
            owner.Player.Walk.Teleport(target.X + 100, target.Y, 0);
            await MapEssences.UseItemOn(owner.Player, Packet(1, stack.Id, target.Id));
            Assert.Equal(3, Left());
            owner.Player.Walk.Teleport(target.X + 8, target.Y, 0);
            var legacy = items.Add(PlayerInventories.MainInventory, 1, 0,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Currency/CurrencyCorruptMonolith")!));
            await MapEssences.UseItemOn(owner.Player, Packet(1, legacy.Id, target.Id));
            Assert.False(Read()[0].Corrupted);
            Assert.Equal(3, Left());
            await MapEssences.UseItemOn(owner.Player, Packet(1, stack.Id, target.Id));
            var changed = Read()[0];
            Assert.True(changed.Corrupted);
            Assert.False(changed.ForcedRelease);
            Assert.Equal(1, changed.Stage);
            Assert.Equal(2, Left());
            await MapEssences.UseItemOn(owner.Player, Packet(1, stack.Id, target.Id));
            Assert.Equal(2, Left());
            var peerItems = Game.ItemsOf(peer.Player);
            var peerVaal = peerItems.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(vaal));
            peer.Player.Walk.Teleport(target.X + 8, target.Y, 0);
            await MapEssences.UseItemOn(peer.Player, Packet(1, peerVaal.Id, target.Id));
            Assert.Contains(peerItems.Items(PlayerInventories.MainInventory), i => i.Id == peerVaal.Id);
            MapEssences.Leave(owner.Player);
            await MapEssences.Enter(owner.Player);
            Assert.True(Read()[0].Corrupted);
            for (ushort click = 0; click < 3; click++) await MapEssences.Use(owner.Player, new(target.Id, 0, click, 0), 0);
            var released = Read()[0];
            var monster = owner.Player.Area.Monsters[released.Monster];
            var rewards = new DropRoll(owner.Player, Game.ModMonster(monster, (released.X, released.Y)));
            MapEssences.Drops(rewards);
            Assert.Equal(changed.Essences.Select(row => MapEssenceCatalog.Find(row)!.Item), rewards.Items);
            await MapEssences.UseItemOn(owner.Player, Packet(1, stack.Id, target.Id));
            Assert.Equal(2, Left());

            var interrupted = Read().First(m => m.Stage < 4 && !m.Corrupted);
            owner.Player.Walk.Teleport(interrupted.X + 8, interrupted.Y, 0);
            var outgoing = Program.Outgoing;
            var creates = new List<byte[]>();
            try
            {
                Program.Outgoing = (player, packet) =>
                {
                    if (ReferenceEquals(player, owner.Player) && packet[0] == 0 && packet[1] == 0xbf)
                        throw new IOException("Interrupted after committing the corruption");
                    outgoing?.Invoke(player, packet);
                };
                await Assert.ThrowsAsync<IOException>(() => MapEssences.UseItemOn(owner.Player,
                    Packet(1, stack.Id, interrupted.Id)));
                Assert.True(Read().Single(m => m.Id == interrupted.Id).Corrupted);
                Assert.Equal(1, Left());
                owner.Player.LeavingArea = true;
                Program.Outgoing = (player, packet) =>
                {
                    if (ReferenceEquals(player, peer.Player)) creates.Add(packet.ToArray());
                    outgoing?.Invoke(player, packet);
                };
                await MapEssences.Tick(peer.Player, 0);
                var prefix = new LoginWriter().U16(0x2bb).U32(interrupted.Id).ToArray();
                Assert.Single(creates, packet => packet.AsSpan().IndexOf(prefix) >= 0);
                creates.Clear();
                await MapEssences.Tick(peer.Player, 0);
                Assert.DoesNotContain(creates, packet => packet.AsSpan().IndexOf(prefix) >= 0);
                peer.Player.Walk.Teleport(interrupted.X + 8, interrupted.Y, 0);
                await MapEssences.UseItemOn(peer.Player, Packet(1, peerVaal.Id, interrupted.Id));
                Assert.Contains(peerItems.Items(PlayerInventories.MainInventory), i => i.Id == peerVaal.Id);
            }
            finally { Program.Outgoing = outgoing; }
        }
        finally
        {
            PlayerInventories.Forget(owner.Player.Character.Name!);
            PlayerInventories.Forget(peer.Player.Character.Name!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    static MonsterModSet RealMod(string id)
    {
        var mod = Program.Items.Mods.ById(id)!;
        Assert.Equal(MonsterModCatalog.Support.Full, MonsterModCatalog.SupportOf(mod));
        return new MonsterModCatalog(Program.Items.Mods, "data").Create(
            [mod.Roll(ModSection.Implicit, new Random(505))], [], MonsterRarity.Normal, 70);
    }

    static void ToughnessEffects()
    {
        foreach (var (toughness, expected) in new[] { (0, 1d), (16, .86), (100, .5), (184, .35), (200, .33), (327, .23), (19900, 0d) })
        {
            var effects = new MonsterModEffects(new Dictionary<uint, int> { [Game.MonsterToughness] = toughness });
            var monster = new SpawnedMonster("toughness", new(0, 0, 0), 60, 8, 10000)
                { Mods = new([], [], effects) };
            Assert.Equal(expected, Game.MonsterToughnessFactor(effects), 8);
            foreach (var type in DamageTypes.ConversionOrder)
            {
                var packet = DamagePacket.Of(type, 10000, 10000);
                Assert.Equal((int)(10000 * expected), Game.MonsterTakesMitigated(monster, packet, false));
                Assert.Equal((int)(10000 * expected), Game.MonsterTakesMitigated(monster, packet, true));
                Assert.Equal(10000 * expected, Game.MonsterTakenComponents(monster, packet, false)[type].Min, 6);
                Assert.Equal(expected, Game.MonsterDotTaken(monster, type), 8);
            }
            Assert.Equal(10000u, monster.MaxLife);
        }
        var mod = Program.Items.Mods.ById("EssenceDaemonModLifeAndDamageLevel5")!;
        var set = Game.MonsterModRolls.Create([mod.Roll(ModSection.Implicit, new Random(505))], [], MonsterRarity.Normal, 70);
        Assert.Equal(184, set.Effects[Game.MonsterToughness]);
        Assert.Equal(.35, Game.MonsterToughnessFactor(set.Effects), 8);
        var shocked = new SpawnedMonster("shocked tough monster", new(0, 0, 0), 60, 8, 10000) { Mods = set };
        Assert.NotEmpty(shocked.Ailments.Inflict(DamagePacket.Of(DamageType.Lightning, 10000, 10000), true,
            shocked.MaxLife, 0, _ => 0, _ => 0, new Random(505)));
        double shock = shocked.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, 1);
        Assert.Equal(.35 * (1 + shock / 100), Game.MonsterDamageTaken(shocked, false, 1), 8);
        Assert.Equal(.35 * (1 + shock / 100), Game.MonsterDotTaken(shocked, DamageType.Fire, 1), 8);
        var player = CombatClient();
        var variety = Game.Monsters.Find("Metadata/Monsters/OriathanCitizen/OriathanCitizenZombieMale1")!;
        var hatred = MapEssenceCatalog.All.Single(e => e.Type == 0 && e.Tier == 5);
        var woe = MapEssenceCatalog.All.Single(e => e.Type == 1 && e.Tier == 5);
        var greed = MapEssenceCatalog.All.Single(e => e.Type == 2 && e.Tier == 5);
        foreach (int[] rows in new[] { new[] { hatred.Row, woe.Row, greed.Row }, new[] { hatred.Row, hatred.Row, hatred.Row } })
        {
            var combined = MapEssenceCatalog.Mods(player, variety, 70, rows);
            Assert.Equal(552, combined.Effects[Game.MonsterToughness]);
            Assert.Equal(.15, Game.MonsterToughnessFactor(combined.Effects), 8);
            var common = combined.Mods.Where(m => m.Hash == mod.Hash).ToArray();
            Assert.Equal(3, common.Length);
            var rarity = Program.Items.Mods.ById("EssenceDaemonModRarityAndQuantityLevel5")!;
            Assert.Equal(3, combined.Mods.Count(m => m.Hash == rarity.Hash));
            Assert.Equal(1500, combined.Effects[Stat.FromLeagueItemRarityPlusPctPermyriad]);
        }
        player.Closed = true;
    }

    static Player CombatClient() => new(new(1, "essence-combat", 300, 300, 100, 100, null, []), 1, 1)
    { Pools = new(70, 10000, 100, 0, 0), PlayerLife = 10000 };

    static void LowLifeEffects()
    {
        var mods = RealMod("EssenceEnrageOnLowLife");
        mods = mods with { Effects = mods.Effects.Combine(new Dictionary<uint, int>
            { [Stat.DamagePlusPct] = 100, [Stat.AttackSpeedPlusPct] = 20, [Stat.BaseCastSpeedPlusPct] = 40, [Stat.BaseMovementVelocityPlusPct] = 20 }) };
        var monster = new SpawnedMonster("enrage", new(10, 10, 36), 60, 8, 100)
        { Mods = mods, Life = 51, Attack = new(1, 1, 8, DamagePacket.Of(DamageType.Physical, 200, 200)) };
        var player = CombatClient();
        player.Area.Monsters[42] = monster;
        DamagePacket Hit() => Game.MonsterModHit(monster, monster.Attack!.Damage, 1000, 1000, new Random(505), out _, now: 0);
        Assert.Equal(200, Hit().MinTotal, 8);
        Assert.Equal(1, Game.MonsterSwingSeconds(player, monster, 0), 8);
        Assert.Equal(20, Game.MaimedMovementFactor(monster, 0).Pct);
        monster.Life = 50;
        Assert.Equal(227, Hit().MinTotal, 8);
        Assert.Equal(2d / 3, Game.MonsterSwingSeconds(player, monster, 0), 8);
        Assert.Equal(80, Game.MaimedMovementFactor(monster, 0).Pct);
        Assert.Equal(1.5, Game.MaimedMovementFactor(monster, 0).Factor, 8);
        Game.MonsterAilmentChangesLocked(player, 0);
        player.Area.Stats.Publish();
        Assert.Contains(player.Area.Delta.Take(), r => r.Bytes.AsSpan().SequenceEqual(InstanceEntities.StatsUpdate(42, [(Stat.MovementVelocityPlusPct, 80)])));
        Assert.Equal(54, monster.Walk.Speed, 8);
        monster.MaimedUntil = 2;
        Assert.Equal(50, Game.MaimedMovementFactor(monster, 0).Pct);
        monster.Attack = monster.Attack! with { Spell = true };
        Assert.Equal(.7, Game.MonsterSwingSeconds(player, monster, 0), 8);
        var ally = new SpawnedMonster("enraged ally", new(10, 10, 36), 60, 8, 100)
            { Hostile = false, Life = 50, Mods = monster.Mods, Attack = monster.Attack };
        Assert.False(Game.EnemyOnLowLife(ally));
        Assert.Equal(227, Game.MonsterModHit(ally, ally.Attack!.Damage, 1000, 1000, new Random(505), out _, now: 0).MinTotal, 8);
        Assert.Equal(.7, Game.MonsterSwingSeconds(player, ally, 0), 8);
        monster.MaimedUntil = 0;
        monster.Life = 51;
        Game.MonsterAilmentChangesLocked(player, 0);
        Assert.Equal(36, monster.Walk.Speed, 8);
        Assert.Equal(200, Hit().MinTotal, 8);
        Assert.Equal(1, Game.MonsterSwingSeconds(player, monster, 0), 8);
    }

    static void ChaosBypassEffects()
    {
        var player = CombatClient();
        Dictionary<uint, int> Stats(params (uint Key, int Value)[] values) =>
            values.ToDictionary(v => v.Key, v => v.Value);
        Assert.True(Game.ChaosDamageBypassesEnergyShield(player, Stats()));
        foreach (uint flag in new[] { Stat.ChaosDamageDoesNotBypassEnergyShield, Stat.VirtualChaosDamageDoesNotBypassEnergyShield })
            Assert.False(Game.ChaosDamageBypassesEnergyShield(player, Stats((flag, 1))));
        var life = Stats((Stat.ChaosDamageDoesNotBypassEnergyShieldWhileNotLowLife, 1));
        player.PlayerLife = player.PlayerMaxLife / 2 + 1;
        Assert.False(Game.ChaosDamageBypassesEnergyShield(player, life));
        player.PlayerLife--;
        Assert.True(Game.ChaosDamageBypassesEnergyShield(player, life));
        life[Stat.LowLifeThresholdPctOverride] = 55;
        Assert.True(Game.ChaosDamageBypassesEnergyShield(player, life));
        player.PlayerLife = player.PlayerMaxLife * 55 / 100 + 1;
        Assert.False(Game.ChaosDamageBypassesEnergyShield(player, life));
        var mana = Stats((Stat.ChaosDamageDoesNotBypassEnergyShieldWhileNotLowMana, 1));
        player.PlayerMana = player.Pools.Mana / 2 + 1;
        Assert.False(Game.ChaosDamageBypassesEnergyShield(player, mana));
        player.PlayerMana--;
        Assert.True(Game.ChaosDamageBypassesEnergyShield(player, mana));
        player.Closed = true;
    }

    static void DecayEffects()
    {
        Assert.Contains("8 seconds", StatDescriptions.Load().For(Program.Stats.Name(Game.DecayOnHit)!)!.Variants[0].Text);
        Assert.Equal("decay", ClientTables.Table("BuffDefinitions")[766].Text("Id"));
        Assert.False(ClientTables.Table("BuffDefinitions")[766].Bool("IsRecovery"));
        var source = new SpawnedMonster("decay", new(0, 0, 0), 60, 8, 100)
            { Mods = RealMod("DecayOnHitEssenceMonster1") };
        foreach (int resistance in new[] { -50, 25, 50, 75, 100 })
        {
            var resisted = CombatClient();
            resisted.Pools = resisted.Pools with { ChaosResistance = resistance };
            var stats = new Dictionary<uint, int>();
            Game.ApplyMonsterDecayLocked(resisted, 42, source, false, 0, stats);
            int first = Game.AdvancePlayerDecayLocked(resisted, 1, stats, false, 1);
            int second = Game.AdvancePlayerDecayLocked(resisted, 2, stats, false, 1);
            Assert.Equal((int)(500 * (1 - Math.Min(resistance, 75) / 100d)), first + second);
            Game.ClearPlayerDecay(resisted);
            resisted.Closed = true;
        }
        var scaled = source.Mods.Effects.Combine(new Dictionary<uint, int>
        {
            [Stat.DamagePlusPct] = 100, [Stat.ChaosDamagePlusPct] = 25, [Stat.DamageOverTimePlusPct] = 25,
            [Stat.PhysicalDamagePlusPct] = 1000, [Stat.SpellDamagePlusPct] = 1000,
            [Stat.BasePhysicalDamagePctToConvertToChaos] = 100, [Stat.PhysicalDamagePctToAddAsFire] = 100,
        });
        Assert.Equal(625, scaled.OverTime(250, DamageType.Chaos), 8);
        var player = CombatClient();
        Dictionary<uint, int> Stats(params (uint Key, int Value)[] values) =>
            values.ToDictionary(v => v.Key, v => v.Value);
        var empty = Stats();
        void Apply(double now, bool blocked = false) => Game.ApplyMonsterDecayLocked(player, 42, source, blocked, now, empty);
        void Advance(double from, double until, Dictionary<uint, int>? stats = null) => Game.AdvancePlayerAilmentsLocked(player, from, until, stats ?? empty);
        Apply(0, true);
        Assert.True(double.IsNegativeInfinity(Game.PlayerDecayStarted(player)));
        Apply(0); Apply(1);
        Assert.All(player.Minions.OutputQueue.Where(q => q.Packet.AsSpan().StartsWith(new byte[] { 1, 0xc1 })),
            q => Assert.Equal(31, q.Packet.Length)); // non-recovery buff: no inline stat values
        Advance(0, 2);
        Assert.Equal(9500u, player.PlayerLife); // strongest only, no poison-style stacking
        Advance(0, 2);
        Assert.Equal(9500u, player.PlayerLife);
        Advance(2, 9);
        Assert.Equal(7750u, player.PlayerLife);
        Advance(9, 10);
        Assert.Equal(7750u, player.PlayerLife);
        Assert.Contains(player.Minions.OutputQueue, q => q.Packet.AsSpan().StartsWith(new byte[] { 1, 0xc1 }));
        Assert.Contains(player.Minions.OutputQueue, q => q.Packet.AsSpan().StartsWith(new byte[] { 1, 0xc2 }));
        Game.ClearPlayerDecay(player);
        player.EnergyShield.SetMaximum(1000, refill: true);
        Apply(20);
        Advance(20, 21);
        Assert.Equal(7500u, player.PlayerLife);
        Assert.Equal(1000u, player.EnergyShield.Current);
        Advance(21, 22, Stats((Stat.ChaosDamageDoesNotBypassEnergyShield, 1)));
        Assert.Equal(7500u, player.PlayerLife);
        Assert.Equal(750u, player.EnergyShield.Current);
        player.Immune = true;
        Advance(22, 24);
        player.Immune = false;
        Advance(24, 25);
        Assert.Equal(7250u, player.PlayerLife);
        Game.ClearPlayerDecay(player);
        Apply(30);
        Advance(30, 31, Stats((Stat.ImmuneToPoison, 1), (Stat.BaseAllAilmentDurationPlusPct, 100)));
        Assert.Equal(7000u, player.PlayerLife); // Decay is neither poison nor an ailment
        Advance(31, 38);
        Assert.Equal(5250u, player.PlayerLife);
        Advance(38, 40);
        Assert.Equal(5250u, player.PlayerLife);
        Game.ClearPlayerDecay(player);
        var slowed = Stats((Stat.BuffTimePassedPlusPctOtherThanTemporalChains, -50));
        Game.ApplyMonsterDecayLocked(player, 42, source, false, 40, slowed);
        Advance(40, 48, slowed);
        Assert.Equal(3250u, player.PlayerLife);
        Advance(48, 56, slowed);
        Assert.Equal(1250u, player.PlayerLife);
        Advance(56, 58, slowed);
        Assert.Equal(1250u, player.PlayerLife);
        Game.ClearPlayerDecay(player);
        Apply(40);
        player.PlayerLife = 1;
        Advance(40, 41);
        Assert.Equal(0u, player.PlayerLife);
        Assert.True(double.IsNegativeInfinity(Game.PlayerDecayStarted(player)));
        Advance(41, 42);
        Assert.True(double.IsNegativeInfinity(Game.PlayerDecayStarted(player)));
        player.Closed = true;
    }

    [GameFilesFact]
    public async Task Essence_monoliths_and_minis_replay_after_leave_and_reentry_without_duplication()
    {
        TestEnvironment.OpenOwnSaves("essence-reentry");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var instance = new InstanceState();
        var world = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var mods = new MapMods(new Dictionary<uint, int> { [Stat.MapExtraMonoliths] = 2 });
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(world.Id, 0x505, 70, stats);
        ServerLevels.For(world.Id, seed, 70);
        var layout = AreaLayouts.For(world.Hash16, seed);
        instance.OpenedWith(new("EssenceReentryOwner", new(layout.Hash, world.Id, "Strand", 3) { Level = 70 }, mods));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        try
        {
            await MapEssences.Enter(owner.Player);
            var monoliths = JsonSerializer.Deserialize<MapEssences.Monolith[]>(instance.ContentState["Essence/monoliths"])!;
            Assert.NotEmpty(monoliths);
            var target = monoliths[0];
            Assert.NotEqual(0x08000000u, target.Id & 0xF8000000u);
            Assert.True(owner.Player.Area.Objects.Has(target.Id));
            foreach (var mini in target.Minis)
            {
                Assert.NotEqual(0x08000000u, mini.Id & 0xF8000000u);
                Assert.True(owner.Player.Area.Objects.Has(mini.Id));
            }

            MapEssences.Leave(owner.Player);
            await using var returning = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
            var previousOwner = GameArea.Own(owner.Player.Area);
            try
            {
                owner.Player.Area.Objects.Join(returning.Player);
                var frame1 = owner.Player.Area.Delta.Take();
                Assert.Contains(frame1, r => r.Entity == target.Id && r.Kind == AreaDelta.Kind.Create
                    && ReferenceEquals(r.Only, returning.Player)
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(r.Bytes) == InstanceEntities.CreateObjectId);
                foreach (var mini in target.Minis)
                    Assert.Contains(frame1, r => r.Entity == mini.Id && r.Kind == AreaDelta.Kind.Create
                        && ReferenceEquals(r.Only, returning.Player)
                        && System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(r.Bytes) == InstanceEntities.CreateObjectId);
            }
            finally { GameArea.Own(previousOwner); }

            var history = Program.HistoryFor(returning.Player.Connection.Session.Ciphers);
            long mark = history.Count;
            await MapEssences.Enter(returning.Player);
            var afterEnter = history.Lines((int)Math.Min(PacketHistory.Capacity, history.Count - mark))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 6 && p[2] == "0x02bb")
                .ToList();
            Assert.Empty(afterEnter);

            returning.Player.Walk.Teleport(target.X + 8, target.Y, 0);
            Assert.True(await MapEssences.Use(returning.Player, new(target.Id, 0, 2, 0), 0));
            var updated = JsonSerializer.Deserialize<MapEssences.Monolith[]>(instance.ContentState["Essence/monoliths"])!;
            Assert.Equal(2, updated[0].Stage);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    sealed class OutcomeRoll(int outcome) : Random
    {
        bool first = true;
        public override int Next(int maxValue)
        {
            int value = first ? outcome : 0;
            first = false;
            return Math.Min(value, maxValue - 1);
        }
    }
}
