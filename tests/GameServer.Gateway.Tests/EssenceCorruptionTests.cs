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
        LowLifeEffects();
        DecayEffects();
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
        var stable = new MapMods(new Dictionary<string, int> { ["map_stable_essence_corruption"] = 1 });
        Assert.Equal(MapEssences.CorruptionOutcome.Upgrade, MapEssences.Corrupt(source, 70, stable, new OutcomeRoll(0))!.Outcome);
        Assert.Equal(MapEssences.CorruptionOutcome.Transform, MapEssences.Corrupt(source, 70, stable, new OutcomeRoll(1))!.Outcome);
        var cannotRelease = new MapMods(new Dictionary<string, int> { ["map_essence_corruption_cannot_release_monsters"] = 1 });
        var prevented = MapEssences.Corrupt(source, 70, cannotRelease, new OutcomeRoll(0))!;
        Assert.Equal(MapEssences.CorruptionOutcome.Release, prevented.Outcome);
        Assert.False(prevented.Monolith.ForcedRelease);
        Assert.Equal(source.Essences, prevented.Monolith.Essences);
        var resonance = new MapMods(new Dictionary<string, int> { ["map_essence_remnant_convert"] = 1 });
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
        var mods = new MapMods(new Dictionary<string, int> { ["map_extra_monoliths"] = 3, ["map_stable_essence_corruption"] = 1 });
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(world.Id, 0x505, 70, stats);
        ServerLevels.For(world.Id, seed, 70);
        var layout = AreaLayouts.For(world.Hash16, seed);
        instance.OpenedWith(new("CorruptionOwner", new(layout.Hash, world.Id, "Strand", 3) { Level = 70 }, mods));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        await using var peer = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        try
        {
            await MapEssences.Enter(owner.Conn);
            await MapEssences.Enter(peer.Conn);
            MapEssences.Monolith[] Read() => JsonSerializer.Deserialize<MapEssences.Monolith[]>(instance.ContentState["Essence/monoliths"])!;
            var target = Read()[0];
            var items = Game.ItemsOf(owner.Conn);
            var vaal = Program.Items.Find(CurrencyEffects.VaalOrb)!;
            var stack = items.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(vaal) with { StackSize = 3 });
            byte[] Packet(uint inventory, uint item, uint targetId) => new LoginWriter().U16(0x74).U32(inventory).U32(item).U32(targetId).ToArray();
            ushort Left() => items.Items(PlayerInventories.MainInventory).Single(i => i.Id == stack.Id).Item.StackSize;
            owner.Conn.Walk.Teleport(target.X + 8, target.Y, 0);
            Assert.False(await MapEssences.UseItemOn(owner.Conn, Packet(1, stack.Id, uint.MaxValue)));
            Assert.True(await MapEssences.UseItemOn(owner.Conn, Packet(2, stack.Id, target.Id)));
            Assert.Equal(3, Left());
            owner.Conn.Walk.Teleport(target.X + 100, target.Y, 0);
            await MapEssences.UseItemOn(owner.Conn, Packet(1, stack.Id, target.Id));
            Assert.Equal(3, Left());
            owner.Conn.Walk.Teleport(target.X + 8, target.Y, 0);
            var legacy = items.Add(PlayerInventories.MainInventory, 1, 0,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Currency/CurrencyCorruptMonolith")!));
            await MapEssences.UseItemOn(owner.Conn, Packet(1, legacy.Id, target.Id));
            Assert.False(Read()[0].Corrupted);
            Assert.Equal(3, Left());
            await MapEssences.UseItemOn(owner.Conn, Packet(1, stack.Id, target.Id));
            var changed = Read()[0];
            Assert.True(changed.Corrupted);
            Assert.False(changed.ForcedRelease);
            Assert.Equal(1, changed.Stage);
            Assert.Equal(2, Left());
            await MapEssences.UseItemOn(owner.Conn, Packet(1, stack.Id, target.Id));
            Assert.Equal(2, Left());
            var peerItems = Game.ItemsOf(peer.Conn);
            var peerVaal = peerItems.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(vaal));
            peer.Conn.Walk.Teleport(target.X + 8, target.Y, 0);
            await MapEssences.UseItemOn(peer.Conn, Packet(1, peerVaal.Id, target.Id));
            Assert.Contains(peerItems.Items(PlayerInventories.MainInventory), i => i.Id == peerVaal.Id);
            MapEssences.Leave(owner.Conn);
            await MapEssences.Enter(owner.Conn);
            Assert.True(Read()[0].Corrupted);
            for (ushort click = 0; click < 3; click++) await MapEssences.Use(owner.Conn, new(target.Id, 0, click, 0), 0);
            var released = Read()[0];
            var monster = owner.Conn.Monsters[released.Monster];
            var rewards = new DropRoll(owner.Conn, Game.ModMonster(monster, (released.X, released.Y)));
            MapEssences.Drops(rewards);
            Assert.Equal(changed.Essences.Select(row => MapEssenceCatalog.Find(row)!.Item), rewards.Items);
            await MapEssences.UseItemOn(owner.Conn, Packet(1, stack.Id, target.Id));
            Assert.Equal(2, Left());

            var interrupted = Read().First(m => m.Stage < 4 && !m.Corrupted);
            owner.Conn.Walk.Teleport(interrupted.X + 8, interrupted.Y, 0);
            var outgoing = Program.Outgoing;
            var creates = new List<byte[]>();
            try
            {
                Program.Outgoing = (cipher, packet) =>
                {
                    if (ReferenceEquals(cipher, owner.Conn.Wire.Ciphers) && packet[0] == 0 && packet[1] == 0xbf)
                        throw new IOException("Interrupted after committing the corruption");
                    outgoing?.Invoke(cipher, packet);
                };
                await Assert.ThrowsAsync<IOException>(() => MapEssences.UseItemOn(owner.Conn,
                    Packet(1, stack.Id, interrupted.Id)));
                Assert.True(Read().Single(m => m.Id == interrupted.Id).Corrupted);
                Assert.Equal(1, Left());
                owner.Conn.LeavingArea = true;
                Program.Outgoing = (cipher, packet) =>
                {
                    if (ReferenceEquals(cipher, peer.Conn.Wire.Ciphers)) creates.Add(packet.ToArray());
                    outgoing?.Invoke(cipher, packet);
                };
                await MapEssences.Tick(peer.Conn, 0);
                var prefix = new LoginWriter().U16(0x2bb).U32(interrupted.Id).ToArray();
                Assert.Single(creates, packet => packet.AsSpan().IndexOf(prefix) >= 0);
                creates.Clear();
                await MapEssences.Tick(peer.Conn, 0);
                Assert.DoesNotContain(creates, packet => packet.AsSpan().IndexOf(prefix) >= 0);
                peer.Conn.Walk.Teleport(interrupted.X + 8, interrupted.Y, 0);
                await MapEssences.UseItemOn(peer.Conn, Packet(1, peerVaal.Id, interrupted.Id));
                Assert.Contains(peerItems.Items(PlayerInventories.MainInventory), i => i.Id == peerVaal.Id);
            }
            finally { Program.Outgoing = outgoing; }
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

    static MonsterModSet RealMod(string id)
    {
        var mod = Program.Items.Mods.ById(id)!;
        Assert.Equal(MonsterModCatalog.Support.Full, MonsterModCatalog.SupportOf(mod));
        return new MonsterModCatalog(Program.Items.Mods, "data").Create(
            [mod.Roll(ModSection.Implicit, new Random(505))], [], MonsterRarity.Normal, 70);
    }

    static ClientState CombatClient() => new(new(1, "essence-combat", 300, 300, 100, 100, null, []), 1, 1)
    { Pools = new(70, 10000, 100, 0, 0), PlayerLife = 10000 };

    static void LowLifeEffects()
    {
        var mods = RealMod("EssenceEnrageOnLowLife");
        mods = mods with { Effects = mods.Effects.Combine(new Dictionary<string, int>
            { ["damage_+%"] = 100, ["attack_speed_+%"] = 20, ["base_cast_speed_+%"] = 40, ["base_movement_velocity_+%"] = 20 }) };
        var monster = new SpawnedMonster("enrage", new(10, 10, 36), 60, 8, 100)
        { Mods = mods, Life = 51, Attack = new(1, 1, 8, DamagePacket.Of(DamageType.Physical, 200, 200)) };
        var conn = CombatClient();
        conn.Monsters[42] = monster;
        DamagePacket Hit() => Game.MonsterModHit(monster, monster.Attack!.Damage, 1000, 1000, new Random(505), out _, now: 0);
        Assert.Equal(200, Hit().MinTotal, 8);
        Assert.Equal(1, Game.MonsterSwingSeconds(conn, monster, 0), 8);
        Assert.Equal(20, Game.MaimedMovementFactor(monster, 0).Pct);
        monster.Life = 50;
        Assert.Equal(227, Hit().MinTotal, 8);
        Assert.Equal(2d / 3, Game.MonsterSwingSeconds(conn, monster, 0), 8);
        Assert.Equal(80, Game.MaimedMovementFactor(monster, 0).Pct);
        Assert.Equal(1.5, Game.MaimedMovementFactor(monster, 0).Factor, 8);
        var packets = Game.MonsterAilmentChangesLocked(conn, 0);
        Assert.Contains(packets, p => p.Packet.AsSpan().SequenceEqual(InstanceEntities.StatsUpdate(42, [(StatKeys.MovementVelocityPct, 80)])));
        Assert.Equal(54, monster.Walk.Speed, 8);
        monster.MaimedUntil = 2;
        Assert.Equal(50, Game.MaimedMovementFactor(monster, 0).Pct);
        monster.Attack = monster.Attack! with { Spell = true };
        Assert.Equal(.7, Game.MonsterSwingSeconds(conn, monster, 0), 8);
        var ally = new SpawnedMonster("enraged ally", new(10, 10, 36), 60, 8, 100)
            { Hostile = false, Life = 50, Mods = monster.Mods, Attack = monster.Attack };
        Assert.False(Game.EnemyOnLowLife(ally));
        Assert.Equal(227, Game.MonsterModHit(ally, ally.Attack!.Damage, 1000, 1000, new Random(505), out _, now: 0).MinTotal, 8);
        Assert.Equal(.7, Game.MonsterSwingSeconds(conn, ally, 0), 8);
        monster.MaimedUntil = 0;
        monster.Life = 51;
        Game.MonsterAilmentChangesLocked(conn, 0);
        Assert.Equal(36, monster.Walk.Speed, 8);
        Assert.Equal(200, Hit().MinTotal, 8);
        Assert.Equal(1, Game.MonsterSwingSeconds(conn, monster, 0), 8);
    }

    static void DecayEffects()
    {
        Assert.Contains("8 seconds", StatDescriptions.Load().For(Game.DecayOnHit)!.Variants[0].Text);
        Assert.Equal("decay", ClientTables.Table("BuffDefinitions")[766].Text("Id"));
        Assert.False(ClientTables.Table("BuffDefinitions")[766].Bool("IsRecovery"));
        var source = new SpawnedMonster("decay", new(0, 0, 0), 60, 8, 100)
            { Mods = RealMod("DecayOnHitEssenceMonster1") };
        var scaled = source.Mods.Effects.Combine(new Dictionary<string, int>
        {
            ["damage_+%"] = 100, ["chaos_damage_+%"] = 25, ["damage_over_time_+%"] = 25,
            ["physical_damage_+%"] = 1000, ["spell_damage_+%"] = 1000,
            ["base_physical_damage_%_to_convert_to_chaos"] = 100, ["physical_damage_%_to_add_as_fire"] = 100,
        });
        Assert.Equal(625, scaled.OverTime(250, DamageType.Chaos), 8);
        var conn = CombatClient();
        Dictionary<uint, int> Stats(params (string Name, int Value)[] values) =>
            values.ToDictionary(v => Program.Stats.Key(v.Name)!.Value, v => v.Value);
        var empty = Stats();
        void Apply(double now, bool blocked = false) => Game.ApplyMonsterDecayLocked(conn, 42, source, blocked, now, empty);
        void Advance(double from, double until, Dictionary<uint, int>? stats = null) => Game.AdvancePlayerAilmentsLocked(conn, from, until, stats ?? empty);
        Apply(0, true);
        Assert.True(double.IsNegativeInfinity(Game.PlayerDecayStarted(conn)));
        Apply(0); Apply(1);
        Assert.All(conn.Minions.OutputQueue.Where(q => q.Packet.AsSpan().StartsWith(new byte[] { 1, 0xc1 })),
            q => Assert.Equal(31, q.Packet.Length)); // non-recovery buff: no inline stat values
        Advance(0, 2);
        Assert.Equal(9500u, conn.PlayerLife); // strongest only, no poison-style stacking
        Advance(0, 2);
        Assert.Equal(9500u, conn.PlayerLife);
        Advance(2, 9);
        Assert.Equal(7750u, conn.PlayerLife);
        Advance(9, 10);
        Assert.Equal(7750u, conn.PlayerLife);
        Assert.Contains(conn.Minions.OutputQueue, q => q.Packet.AsSpan().StartsWith(new byte[] { 1, 0xc1 }));
        Assert.Contains(conn.Minions.OutputQueue, q => q.Packet.AsSpan().StartsWith(new byte[] { 1, 0xc2 }));
        Game.ClearPlayerDecay(conn);
        conn.EnergyShield.SetMaximum(1000, refill: true);
        Apply(20);
        Advance(20, 21);
        Assert.Equal(7500u, conn.PlayerLife);
        Assert.Equal(1000u, conn.EnergyShield.Current);
        Advance(21, 22, Stats(("chaos_damage_does_not_bypass_energy_shield", 1)));
        Assert.Equal(7500u, conn.PlayerLife);
        Assert.Equal(750u, conn.EnergyShield.Current);
        conn.Immune = true;
        Advance(22, 24);
        conn.Immune = false;
        Advance(24, 25);
        Assert.Equal(7250u, conn.PlayerLife);
        Game.ClearPlayerDecay(conn);
        Apply(30);
        Advance(30, 31, Stats(("immune_to_poison", 1), ("base_all_ailment_duration_+%", 100)));
        Assert.Equal(7000u, conn.PlayerLife); // Decay is neither poison nor an ailment
        Advance(31, 38);
        Assert.Equal(5250u, conn.PlayerLife);
        Advance(38, 40);
        Assert.Equal(5250u, conn.PlayerLife);
        Game.ClearPlayerDecay(conn);
        var slowed = Stats(("buff_time_passed_+%_other_than_temporal_chains", -50));
        Game.ApplyMonsterDecayLocked(conn, 42, source, false, 40, slowed);
        Advance(40, 48, slowed);
        Assert.Equal(3250u, conn.PlayerLife);
        Advance(48, 56, slowed);
        Assert.Equal(1250u, conn.PlayerLife);
        Advance(56, 58, slowed);
        Assert.Equal(1250u, conn.PlayerLife);
        Game.ClearPlayerDecay(conn);
        Apply(40);
        conn.PlayerLife = 1;
        Advance(40, 41);
        Assert.Equal(0u, conn.PlayerLife);
        Assert.True(double.IsNegativeInfinity(Game.PlayerDecayStarted(conn)));
        Advance(41, 42);
        Assert.True(double.IsNegativeInfinity(Game.PlayerDecayStarted(conn)));
        conn.Closed = true;
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
