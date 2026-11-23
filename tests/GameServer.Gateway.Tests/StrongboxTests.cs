namespace GameServer.Gateway.Tests;

using System.Buffers.Binary;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

public sealed class StrongboxTests
{
    // Currency on a closed box changes its rarity and chest mods as on an item, and goes on the wire with them; a Vaal Orb
    // locks it; an opened box takes nothing.
    [Fact]
    public void Currency_changes_a_closed_strongbox_like_an_item()
    {
        var type = StrongboxCatalog.Find("Metadata/Chests/StrongBoxes/Arcanist")!;
        var catalog = Program.Items.Mods;
        var rng = new Random(7);
        (MapStrongboxes.Box? Box, string? Why) Use(MapStrongboxes.Box box, string currency) =>
            MapStrongboxes.Change(catalog, type, box, currency, 70, MapMods.None, rng);
        var normal = new MapStrongboxes.Box(1, 0, 100, 100, type.Id);

        Assert.Null(Use(normal, CurrencyEffects.OrbOfAlteration).Box);
        var magic = Use(normal, CurrencyEffects.OrbOfTransmutation).Box!;
        Assert.Equal(1, magic.Rarity);
        Assert.InRange(magic.Mods.Length, 1, 2);
        Assert.All(magic.Mods, m => Assert.Equal(StrongboxMods.ChestDomain, catalog.ByHash(m.Hash)!.Domain));
        Assert.NotEqual(StrongboxCatalog.Blob(type, 100, 100, false, false),
            StrongboxCatalog.Blob(type, 100, 100, false, false, magic.Rarity, [.. type.Mods, .. magic.Explicits]));
        var rare = Use(magic, CurrencyEffects.RegalOrb).Box!;
        Assert.Equal(2, rare.Rarity);
        Assert.Equal(magic.Mods.Length + 1, rare.Mods.Length);
        var annulled = Use(rare, CurrencyEffects.OrbOfAnnulment).Box!;
        Assert.Equal(rare.Mods.Length - 1, annulled.Mods.Length);
        Assert.All(annulled.Mods, m => Assert.Contains(rare.Mods, r => r.Hash == m.Hash));
        Assert.Equal(rare.Mods.Select(m => m.Hash), Use(rare, CurrencyEffects.DivineOrb).Box!.Mods.Select(m => m.Hash));
        var scoured = Use(rare, CurrencyEffects.OrbOfScouring).Box!;
        Assert.Equal(0, scoured.Rarity);
        Assert.Empty(scoured.Mods);

        var corrupted = Use(rare, CurrencyEffects.VaalOrb).Box!;
        Assert.True(corrupted.Corrupted);
        Assert.Equal(rare.Mods, corrupted.Mods);
        var chanced = Use(normal, CurrencyEffects.OrbOfChance).Box!;
        Assert.InRange(chanced.Rarity, 1, 3);
        Assert.NotEmpty(chanced.Mods);
        Assert.Equal(chanced.Rarity == 3, chanced.Unique is not null);
        Assert.Equal("the box is corrupted", Use(corrupted, CurrencyEffects.ChaosOrb).Why);
        Assert.Equal("the box is open", Use(normal with { Phase = 1 }, CurrencyEffects.OrbOfTransmutation).Why);
    }

    // A unique box is a UniqueChests row on its strongbox: rarity 3, its own model and its Words name on the wire.
    [Fact]
    public void Unique_strongboxes_come_from_the_unique_chests()
    {
        var kaom = StrongboxCatalog.FindUnique("KaomsCache")!;
        Assert.Equal("Kaom's Cache", kaom.Name);
        var type = StrongboxCatalog.All.Single(t => t.Row == kaom.ChestRow);
        Assert.Equal("Metadata/Chests/StrongBoxes/Armory", type.Id);
        Assert.DoesNotContain(StrongboxCatalog.Uniques, u => u.Id.StartsWith("Grandmaster", StringComparison.Ordinal));
        Assert.All(Enumerable.Range(0, 50), i => Assert.True(StrongboxCatalog.RollUnique(30, new Random(i))!.Value.Unique.MinLevel <= 30));
        var plain = StrongboxCatalog.Blob(type, 100, 100, false, false, 3, type.Mods, null, null);
        var named = StrongboxCatalog.Blob(type, 100, 100, false, false, 3, type.Mods, null, kaom);
        Assert.NotEqual(plain, named);
        // A non-normal box's inherent bonus: the hidden ChestMagic/ChestRare/ChestUnique mods.
        Assert.Equal(0, StrongboxMods.RarityStat(Program.Items.Mods, 0, "chest_hidden_item_quantity_+%"));
        Assert.Equal(100, StrongboxMods.RarityStat(Program.Items.Mods, 2, "chest_hidden_item_quantity_+%"));
        Assert.Equal(150, StrongboxMods.RarityStat(Program.Items.Mods, 3, "chest_hidden_item_rarity_+%"));
        var word = BitConverter.GetBytes(kaom.NameWord);
        Assert.True(named.AsSpan().IndexOf(word) >= 0 || named.AsSpan().IndexOf(word.Reverse().ToArray()) >= 0);
    }

    // A Vaal Orb right-clicked in the currency stash tab and clicked on a closed box (C→S 0x74 with the tab's inventory id)
    // corrupts the box and consumes one orb from that tab — the case the character-inventory-only handler refused.
    [GameFilesFact]
    public async Task Currency_from_the_stash_tab_corrupts_a_closed_box()
    {
        TestEnvironment.OpenOwnSaves("strongbox-stash");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var instance = new InstanceState();
        var world = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 70);
        var mods = MapMods.None;
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 3, new Dictionary<uint, int>(), new HashSet<string>(), -1));
        uint seed = ServerLevels.PlayableSeed(world.Id, 0x506, 70, stats);
        ServerLevels.For(world.Id, seed, 70);
        var layout = AreaLayouts.For(world.Hash16, seed);
        instance.OpenedWith(new("BoxOwner", new(layout.Hash, world.Id, "Strand", 3) { Level = 70 }, mods));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        try
        {
            Assert.True(await MapStrongboxes.SpawnAt(owner.Player,
                [new MapStrongboxes.SpawnRequest(100, 100, StrongboxCatalog.Contents.Currency, 70, MapMods.None)], new Random(7)) > 0);
            var box = ReadBoxes(owner.Player)[0]!;
            uint id = box["Id"]!.GetValue<uint>();
            Assert.NotEqual(3, box["Rarity"]!.GetValue<byte>()); // a unique box takes no currency; this seed rolls none

            var catalog = Program.Items;
            var vaal = catalog.Find(CurrencyEffects.VaalOrb)!;
            var stash = Game.StashOf(owner.Player);
            var item = ItemCatalog.Create(vaal) with { StackSize = 5 };
            Assert.NotNull(stash.AddStacked(0, item, i => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null, out _));
            uint orb = stash.Inventory(0).Items.Single(i => i.Item.TypeHash == vaal.Hash).Id;
            Assert.Equal(5, stash.CountOf(vaal.Hash));

            Assert.True(await MapStrongboxes.UseItemOn(owner.Player, UseMsg(stash.InventoryOf(0), orb, id)));
            Assert.True(ReadBoxes(owner.Player)[0]!["Corrupted"]!.GetValue<bool>());
            Assert.Equal(4, stash.CountOf(vaal.Hash));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    // The closed glow and the open effect a box plays come from the Chests row's ChestEffects row, one model per
    // rarity (the rows' white/blue/gold/brown sets; the small and big strongboxes have their own).
    [Fact]
    public void Chest_effects_come_from_the_chests_row_by_rarity()
    {
        var small = StrongboxCatalog.Find("Metadata/Chests/StrongBoxes/Arcanist")!;
        Assert.Equal(2, small.ChestEffect);
        Assert.EndsWith("smlstrongboxwhiteloopfx.ao", StrongboxCatalog.ClosedEffect(small, 0));
        Assert.EndsWith("smlstrongboxblueloopfx.ao", StrongboxCatalog.ClosedEffect(small, 1));
        Assert.EndsWith("smlstrongboxgoldopenfx.ao", StrongboxCatalog.OpenEffect(small, 2));
        Assert.EndsWith("smlstrongboxbrownopenfx.ao", StrongboxCatalog.OpenEffect(small, 3));
        var big = StrongboxCatalog.Find("Metadata/Chests/StrongBoxes/Large")!;
        Assert.Equal(1, big.ChestEffect);
        Assert.EndsWith("bigstrongboxwhiteloopfx.ao", StrongboxCatalog.ClosedEffect(big, 0));
        Assert.DoesNotContain("loop", StrongboxCatalog.OpenEffect(big, 3));
    }

    static JsonNode ReadBoxes(Player player)
    {
        var state = new JsonObject();
        MapStrongboxes.Describe(player, state);
        return state["strongboxes"]!;
    }

    static byte[] UseMsg(uint inventory, uint item, uint target)
    {
        var b = new byte[InstanceItems.UseItemOnObjectLength];
        BinaryPrimitives.WriteUInt16BigEndian(b, InstanceItems.UseItemOnObjectId);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(2), inventory);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(6), item);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(10), target);
        return b;
    }
}
