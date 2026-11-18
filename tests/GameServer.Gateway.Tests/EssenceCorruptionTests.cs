using System.Text.Json;
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
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1, false));
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
