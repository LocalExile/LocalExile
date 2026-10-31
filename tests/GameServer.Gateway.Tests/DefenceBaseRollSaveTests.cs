using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class DefenceBaseRollSaveTests : IDisposable
{
    readonly string _character = "DefenceRoll" + Guid.NewGuid().ToString("N");
    readonly string _account = "DefenceRollStash" + Guid.NewGuid().ToString("N");

    public DefenceBaseRollSaveTests() => TestSaves.Ensure();

    static Item Armour(byte roll) => ItemCatalog.Create(Program.Items.Find("Scale Vest")!, defenceBaseRoll: roll);

    static PlayerInventories.Shape? Shape(Item item) => Program.Items.ByHash(item.TypeHash) is { } type
        ? new(type.Width, type.Height, type.Class) : null;

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    public void Saved_item_JSON_round_trip_preserves_the_exact_Armour_component_byte(byte roll)
    {
        var item = Armour(roll) with { ItemLevel = 20, Quality = 17 };
        var saved = CharacterSaves.ToSaved(item, Program.Items.Mods);
        string json = JsonSerializer.Serialize(saved, CharacterSaves.Opts);
        var fromJson = JsonSerializer.Deserialize<CharacterSaves.SavedItem>(json, CharacterSaves.Opts)!;
        var restored = CharacterSaves.FromSaved(fromJson, Program.Items);

        Assert.Equal(roll, saved.DefenceBaseRoll);
        Assert.Equal(roll, fromJson.DefenceBaseRoll);
        Assert.Equal(roll, restored.DefenceBaseRoll);
        Assert.Equal(item.Payload(), restored.Payload());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_items_without_a_saved_roll_keep_the_original_minimum_instead_of_rerolling(bool legacyMods)
    {
        var json = JsonSerializer.SerializeToNode(CharacterSaves.ToSaved(Armour(31), Program.Items.Mods))!.AsObject();
        Assert.True(json.Remove(nameof(CharacterSaves.SavedItem.DefenceBaseRoll)));
        if (legacyMods) Assert.True(json.Remove(nameof(CharacterSaves.SavedItem.Mods)));
        var saved = json.Deserialize<CharacterSaves.SavedItem>(CharacterSaves.Opts)!;

        Assert.Equal((byte)0, saved.DefenceBaseRoll);
        for (int i = 0; i < 16; i++)
        {
            var restored = CharacterSaves.FromSaved(saved, Program.Items);
            Assert.Equal((byte)0, restored.DefenceBaseRoll);
            Assert.Equal((byte)0, CharacterSaves.ToSaved(restored, Program.Items.Mods).DefenceBaseRoll);
        }
    }

    [Fact]
    public void Character_inventory_reload_keeps_the_roll_item_id_and_cell()
    {
        CharacterSaves.Write(_character, new CharacterSaves.Save([]));
        var inventory = PlayerInventories.For(_character, Program.Items);
        var placed = inventory.Add(PlayerInventories.MainInventory, 3, 1, Armour(19) with { Quality = 20 });
        PlayerInventories.Forget(_character);

        var restored = Assert.Single(PlayerInventories.For(_character, Program.Items).Items(PlayerInventories.MainInventory));
        Assert.Equal(placed.Id, restored.Id);
        Assert.Equal((byte)3, restored.X);
        Assert.Equal((byte)1, restored.Y);
        Assert.Equal((byte)19, restored.Item.DefenceBaseRoll);
        Assert.Equal(placed.Item.Payload(), restored.Item.Payload());
    }

    [Fact]
    public void Shared_stash_reload_keeps_the_roll_including_the_special_wire_sentinel()
    {
        var stash = AccountStash.For(_account, Program.Items);
        var placed = stash.Place(0, 2, 3, new InventoryItem(1, 0, 0, Armour(32)), Shape, out string why);
        Assert.NotNull(placed);
        Assert.Equal("", why);
        AccountStash.Forget(_account);

        var restored = Assert.Single(AccountStash.For(_account, Program.Items).Inventory(0).Items);
        Assert.Equal(placed!.Value.Placed.Id, restored.Id);
        Assert.Equal((byte)2, restored.X);
        Assert.Equal((byte)3, restored.Y);
        Assert.Equal((byte)32, restored.Item.DefenceBaseRoll);
    }

    [Fact]
    public void Armour_with_a_socketed_gem_keeps_its_roll_and_embedded_item_state()
    {
        var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!)
            with { Quality = 8, GemFlags = 4, GemExperience = 1200 };
        var item = Armour(23) with { Sockets = [new(SocketColour.Blue, gem)], Links = [1] };
        var json = JsonSerializer.Serialize(CharacterSaves.ToSaved(item, Program.Items.Mods), CharacterSaves.Opts);
        var restored = CharacterSaves.FromSaved(JsonSerializer.Deserialize<CharacterSaves.SavedItem>(json)!, Program.Items);

        Assert.Equal((byte)23, restored.DefenceBaseRoll);
        Assert.Equal(gem.Payload(), Assert.Single(restored.Sockets).Gem!.Payload());
        Assert.Equal(item.Payload(), restored.Payload());
    }

    [Theory]
    [InlineData(13)]
    [InlineData(32)]
    public void Quality_affix_reforges_and_mirroring_preserve_the_existing_base_roll(byte roll)
    {
        var item = Armour(roll) with { ItemLevel = 20 };
        var withQuality = CurrencyEffects.ArmourerScrap(item)!;
        var reforged = new ItemRolls(Program.Items).Roll(withQuality, ItemRarity.Magic, new Random(75));
        var mirrored = CurrencyEffects.Mirror(reforged, new ItemRolls(Program.Items))!;

        Assert.Equal((byte)1, withQuality.Quality);
        Assert.Equal(roll, withQuality.DefenceBaseRoll);
        Assert.Equal(roll, reforged.DefenceBaseRoll);
        Assert.Equal(roll, mirrored.DefenceBaseRoll);
        Assert.True(mirrored.Mirrored);
        Assert.Equal(roll, CharacterSaves.FromSaved(CharacterSaves.ToSaved(mirrored, Program.Items.Mods), Program.Items)
            .DefenceBaseRoll);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_character);
        AccountStash.Forget(_account);
    }
}
